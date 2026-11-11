using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Inference;

public sealed record ChatMessage(string Role, string Text, byte[]? Image = null, bool IsHistory = false)
{
    public string ImageMediaType { get; init; } = "image/png";
}
public enum GenerationPurpose { Dialogue, Summary, Metadata, ToolPlanning, Sketch, WorkTask, StandardDialogue, DeepDialogue, WorkStep, DeepWorkStep, RequestedSketch, SketchReview, WorkSearch, SketchRevision, SketchInspection, ScreenRelevance, PersonalThought, PersonalOutline, PersonalScene, PersonalReview }
public interface IChatModel
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct);
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, GenerationPurpose purpose, CancellationToken ct)=>CompleteAsync(messages,
        purpose is GenerationPurpose.Metadata or GenerationPurpose.ToolPlanning or GenerationPurpose.Sketch or GenerationPurpose.RequestedSketch or GenerationPurpose.SketchReview or GenerationPurpose.SketchRevision or GenerationPurpose.WorkTask
            or GenerationPurpose.WorkStep or GenerationPurpose.DeepWorkStep or GenerationPurpose.ScreenRelevance
            or GenerationPurpose.PersonalOutline or GenerationPurpose.PersonalReview,ct);
}

public interface ITurnContextModel
{
    Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages,CancellationToken ct);
    Task<string?> PlanSpeechAsync(IReadOnlyList<ChatMessage> messages,int sentenceCount,CancellationToken ct)=>Task.FromResult<string?>(null);
}

internal interface IPublicationIntentModel
{
    Task<string> InterpretPublicationAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

internal interface IReplyTaskModel
{
    Task<string> ReviewTaskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

public sealed class LocalModel(PortablePaths paths, ModelOptions options, CancellationToken stopping = default) : IChatModel, ITurnContextModel, IPublicationIntentModel, IReplyTaskModel, IDeepAnalysisModel, IAsyncDisposable
{
    private static readonly JsonElement ReplyTaskSchema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["action","evidence","previousEvidence","commitment","title","nextStep","existing","route","target","operations","defer"],"properties":{
          "action":{"type":"string","enum":["none","google_ai","owner_task","personal_task","unverified","repeat_task","revise_reply"]},
          "evidence":{"type":"string","maxLength":300},"commitment":{"type":"string","maxLength":300},
          "previousEvidence":{"type":"string","maxLength":300},
          "title":{"type":"string","maxLength":100},"nextStep":{"type":"string","maxLength":300},
          "existing":{"type":"integer","minimum":0,"maximum":9},
          "route":{"type":"string","enum":["note","work","search_web","read_web","read_browser","read_file","screen","diary_titles","publication"]},
          "target":{"type":"string","maxLength":1200},"operations":{"type":"array","maxItems":8,
            "items":{"type":"string","enum":["open_app","desktop_input","read_file","write_file","search_web","read_browser","draw","copy_text"]}},
          "defer":{"type":"boolean"}}}
        """);
    private static readonly JsonElement PublicationIntentSchema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["action","material","site","siteReference","evidence","another","signatureEvidence"],"properties":{
          "action":{"type":"string","enum":["none","publish","offer","approve","retry","status"]},
          "material":{"type":"string","enum":["none","same","writing","reading_note"]},
          "site":{"type":"string","maxLength":253},"siteReference":{"type":"string","maxLength":180},
          "evidence":{"type":"string","maxLength":180},"another":{"type":"boolean"},"signatureEvidence":{"type":"string","maxLength":180}
        }}
        """);
    private static readonly JsonElement ContextSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["referent","intent","request","uncertainty","evidence","effort","operations","drawingSubject","continuesPrevious","previousEvidence"],"properties":{
      "referent":{"type":"string","enum":["current_screen","text","unknown","none"]},
      "intent":{"type":"string","enum":["chat","screen","screen_research","screen_followup","file_capability","web_capability","lookup_term","work","revise","repeat_task"]},
      "effort":{"type":"string","enum":["quick","standard","deep"]},
      "request":{"type":"string","maxLength":450},"uncertainty":{"type":"string","maxLength":180},"evidence":{"type":"string","maxLength":120},
      "operations":{"type":"array","maxItems":8,"items":{"type":"string","enum":["open_app","desktop_input","read_file","write_file","search_web","read_browser","draw","copy_text"]}},
      "drawingSubject":{"type":"string","maxLength":500},"continuesPrevious":{"type":"boolean"},"previousEvidence":{"type":"string","maxLength":120}
    }}
    """);
    private static readonly JsonElement ScreenRelevanceSchema=JsonSerializer.Deserialize<JsonElement>(
        """{"type":"object","additionalProperties":false,"required":["relevant"],"properties":{"relevant":{"type":"boolean"}}}""");
    private static readonly JsonElement PersonalOutlineSchema=JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["title","aim","scenes"],"properties":{
          "title":{"type":"string","maxLength":80},"aim":{"type":"string","maxLength":500},
          "scenes":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"string","maxLength":600}}
        }}
        """);
    private static readonly JsonElement PersonalReviewSchema=JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["decision","feedback","scene"],"properties":{
          "decision":{"type":"string","enum":["accept","revise","research","defer","keep"]},
          "feedback":{"type":"string","maxLength":500},"scene":{"type":"integer","minimum":0,"maximum":3}
        }}
        """);
    private static readonly JsonElement[] SpeechSchemas=Enumerable.Range(1,SpeechDelivery.MaxDirectedSentences)
        .Select(count=>JsonSerializer.SerializeToElement(new
        {
            type="object",additionalProperties=false,required=new[]{"emotions"},properties=new
            {
                emotions=new {type="array",minItems=count,maxItems=count,items=new {type="string",@enum=SpeechDelivery.EmotionNames}}
            }
        })).ToArray();
    private static readonly JsonElement SketchSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["title","background","shapes"],"properties":{
      "title":{"type":"string","maxLength":80},"background":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"},
      "shapes":{"type":"array","minItems":3,"maxItems":36,"items":{"type":"object","additionalProperties":false,
        "required":["kind","color"],"properties":{
          "kind":{"type":"string","enum":["rectangle","ellipse","triangle","polygon"]},"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"},
          "x":{"type":"integer","minimum":0,"maximum":99},"y":{"type":"integer","minimum":0,"maximum":99},
          "w":{"type":"integer","minimum":1,"maximum":100},"h":{"type":"integer","minimum":1,"maximum":100},
          "points":{"type":"array","minItems":3,"maxItems":24,"items":{"type":"array","minItems":2,"maxItems":2,"items":{"type":"integer","minimum":0,"maximum":100}}}
        }}}
    }}
    """);
    private static readonly JsonElement SketchReviewSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["matches","reason"],"properties":{
      "matches":{"type":"boolean"},"reason":{"type":"string","maxLength":180}
    }}
    """);
    private static readonly JsonElement WorkTaskSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["matches","title","goal","progress","nextStep","references"],"properties":{
      "matches":{"type":"array","maxItems":8,"items":{"type":"integer","minimum":1}},
      "title":{"type":"string","maxLength":100},"goal":{"type":"string","maxLength":500},
      "progress":{"type":"string","maxLength":1000},"nextStep":{"type":"string","maxLength":500},
      "references":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":500}}
    }}
    """);
    private static readonly JsonElement WorkStepSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["plan","action","reply"],"properties":{
      "plan":{"type":"array","maxItems":5,"items":{"type":"string","maxLength":160}},
      "action":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":["tool","args"],"properties":{
        "tool":{"type":"string","maxLength":32},"args":{"type":"object","additionalProperties":true}}}]},
      "reply":{"type":"string","maxLength":3600}
    }}
    """);
    private static readonly JsonElement DecisionSchema=JsonSerializer.Deserialize<JsonElement>($$$$$"""
    {"type":"object","additionalProperties":false,"required":["growth","speak","reply","memory","emotion","relationship","actions","reaction"],"properties":{
      "growth":{"anyOf":[{"type":"object","additionalProperties":false,"required":["action","kind","name","expression","evidence"],"properties":{
        "action":{"type":"string","enum":["explore","reinforce","reconsider"]},"kind":{"type":"string","enum":["habit","interest","taste"]},
        "name":{"type":"string","maxLength":64},"expression":{"type":"string","maxLength":160},"evidence":{"type":"string","maxLength":120}
      }},{"type":"null"}]},
      "speak":{"type":"boolean"},"reply":{"type":"string","maxLength":4000},"memory":{"type":"string","maxLength":1000},
      "emotion":{"type":"object","additionalProperties":false,"required":["warmth","trust","closeness","valence","energy","stress","curiosity","reason"],"properties":{
        "warmth":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"trust":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"closeness":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},
        "valence":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"energy":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"stress":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"curiosity":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"reason":{"type":"string","maxLength":160}}},
      "relationship":{"type":"object","additionalProperties":false,"required":["currentAttitude","recentImpression","appreciates","concerns","boundaries"],"properties":{"currentAttitude":{"type":["string","null"],"maxLength":500},"recentImpression":{"type":["string","null"],"maxLength":300},"appreciates":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}},"concerns":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}},"boundaries":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}}}},
      "actions":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,"required":["tool","args"],"properties":{"tool":{"type":"string","maxLength":80},"args":{"type":"object"}}}},
      "reaction":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":["feelings","detail"],"properties":{
        "feelings":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","enum":{{{{{JsonSerializer.Serialize(RecentReaction.Labels)}}}}}}},
        "detail":{"type":"string","minLength":6,"maxLength":120}
      }}]}
    }}
    """);
    private readonly HttpClient client = new() { BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
    private Process? process;
    private Task? drainTask;
    private bool cpuOnly;
    internal string? LoadedAdapter { get; private set; }
    private int incognito;
    private int suppressRuntimeLogs;

    internal void SetIncognito(bool active)
    {
        // A child process can flush buffered diagnostics after a request has returned.
        if (active) Volatile.Write(ref suppressRuntimeLogs, 1);
        Volatile.Write(ref incognito, active ? 1 : 0);
    }
    public Task StartAsync(CancellationToken ct) => StartAsync(null, false, ct);

    internal async Task StartAsync(string? adapter, bool useCpu, CancellationToken ct)
    {
        if (!options.AutoStart) { await WaitReadyAsync(ct); return; }
        if (await HealthyAsync(ct)) throw new IOException("Model port is already occupied. Stop the existing server, use another endpoint, or explicitly set model.autoStart=false.");
        var modelPath = paths.Inside(options.EffectiveModelFile);
        var projectorPath = paths.Inside(options.EffectiveProjectorFile);
        foreach (var file in new[] { paths.Inside(options.Executable), modelPath, projectorPath })
            if (!File.Exists(file)) throw new FileNotFoundException(
                $"Local dependency missing: {Path.GetFileName(file)}. Run Setup.cmd or Setup.cmd -Only Abliteration for the abliterated weights.", file);
        var info = new ProcessStartInfo(paths.Inside(options.Executable)) { WorkingDirectory = paths.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var endpoint = new Uri(options.Endpoint);
        string[] arguments = ["--model", modelPath, "--mmproj", projectorPath, "--host", "127.0.0.1", "--port", endpoint.Port.ToString(), "--ctx-size", options.ContextSize.ToString(), "--n-gpu-layers", (useCpu ? 0 : options.GpuLayers).ToString(), "--threads", options.Threads.ToString(), "--parallel", "1", "--batch-size", "512", "--ubatch-size", "128", "--image-max-tokens", "1024", "--no-webui", "--offline", "--jinja", "--alias", "neighbour"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--poll", "0", "--poll-batch", "0", "--image-min-tokens", "1024", "--cache-ram", "0", "--cors-origins", "http://127.0.0.1", "--reasoning-format", "deepseek" }) info.ArgumentList.Add(argument);
        if (useCpu)
            foreach (var argument in new[] { "--device", "none", "--no-mmproj-offload", "--no-op-offload" }) info.ArgumentList.Add(argument);
        if (adapter is not null)
        {
            // The per-request scale is opt-in. The CLI uses ':' as its separator, so pass a root-relative path on Windows.
            info.ArgumentList.Add("--lora-scaled");
            info.ArgumentList.Add(Path.GetRelativePath(paths.Root, adapter) + ":0");
        }
        process = Process.Start(info) ?? throw new IOException("Cannot start llama-server.");
        ChildProcessJob.Attach(process);
        drainTask = Task.WhenAll(DrainAsync(process.StandardOutput, "llama.stdout.log"), DrainAsync(process.StandardError, "llama.stderr.log"));
        try
        {
            await WaitReadyAsync(ct);
            if (adapter is not null)
            {
                using var response = await client.GetAsync("lora-adapters", ct);
                response.EnsureSuccessStatusCode();
                using var loaded = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (loaded.RootElement.GetArrayLength() != 1 || loaded.RootElement[0].GetProperty("id").GetInt32() != 0 ||
                    loaded.RootElement[0].GetProperty("scale").GetDouble() != 0 ||
                    loaded.RootElement[0].GetProperty("path").GetString() is not { } registeredPath ||
                    !string.Equals(Path.GetFullPath(registeredPath, paths.Root), adapter, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("llama-server did not register the requested LoRA adapter.");
            }
            LoadedAdapter = adapter;
            cpuOnly = useCpu;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    internal async Task ReloadAsync(string? adapter, bool useCpu, CancellationToken ct)
    {
        if (!options.AutoStart) throw new InvalidOperationException("LoRA requires the llama-server started by ComputerNeighbour.");
        if (process is { HasExited: false } && adapter == LoadedAdapter && useCpu == cpuOnly) return;
        var previousAdapter = LoadedAdapter;
        var previousCpu = cpuOnly;
        await StopAsync();
        try { await StartAsync(adapter, useCpu, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await StartAsync(previousAdapter, previousCpu, ct);
            throw;
        }
    }

    internal string DeepModelName => OllamaDeepModel.ModelName(options.UseAbliterated);
    internal bool DeepReady => !cpuOnly && options.Deep.Enabled && options.AutoStart && OllamaDeepModel.IsInstalled(paths, options.UseAbliterated);

    async Task<string?> IDeepAnalysisModel.AnalyzeDeepAsync(string request, Action<string> status, CancellationToken ct)
    {
        if (Volatile.Read(ref incognito) != 0 || !DeepReady) return null;
        ct.ThrowIfCancellationRequested();
        var adapter = LoadedAdapter;
        status($"[Deep] Consulting {DeepModelName} locally. This can take a few minutes. /cancel stops the request.");
        try
        {
            await StopAsync();
            var advice = await OllamaDeepModel.AnalyzeAsync(paths, options.UseAbliterated, request, options.Deep.TimeoutSeconds, ct);
            status("[Deep] Qwen3.8 analysis ready. Returning to the regular model.");
            return advice;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status("[Deep] Qwen3.8 reached its time limit. Continuing on the regular model.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or JsonException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            status("[Deep] Qwen3.8 could not finish. Continuing on the regular model. " + ex.Message);
            return null;
        }
        finally
        {
            if (!stopping.IsCancellationRequested)
            {
                try { await StartAsync(adapter, false, stopping); }
                catch (Exception ex) when (!stopping.IsCancellationRequested && options.GpuLayers != 0 &&
                    ex is (IOException or HttpRequestException or OperationCanceledException))
                {
                    status("[Deep] Restoring the regular model on the CPU.");
                    await StartAsync(adapter, true, stopping);
                }
            }
        }
    }
    private async Task DrainAsync(StreamReader reader, string filename)
    {
        try
        {
            if (Volatile.Read(ref suppressRuntimeLogs) != 0)
            {
                while (await reader.ReadLineAsync() is not null) { }
                return;
            }
            var file = paths.Inside("data/logs/" + filename);
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".previous", true);
            await using var writer = new StreamWriter(file, append: true) { AutoFlush = true };
            long written = 0;
            while (await reader.ReadLineAsync() is { } line)
                if (Volatile.Read(ref suppressRuntimeLogs) == 0 && written < 4_000_000)
                { await writer.WriteLineAsync(line); written += line.Length; }
        }
        catch (IOException) { }
    }
    private async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try { using var response = await client.GetAsync("health", ct); return response.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
    private async Task WaitReadyAsync(CancellationToken ct)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        while (!await HealthyAsync(startup.Token))
        {
            if (process?.HasExited == true) throw new IOException("llama-server exited. See data/logs/llama.stderr.log; try model.gpuLayers=0 or CPU setup.");
            await Task.Delay(500, startup.Token);
        }
    }
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)=>RequestAsync(messages,json,json?0.25:0.25,false,ct);
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,GenerationPurpose purpose,CancellationToken ct)=>CompleteAsync(messages,purpose,ct,false);
    internal Task<string> CompletePersonalDialogueAsync(IReadOnlyList<ChatMessage> messages,GenerationPurpose purpose,CancellationToken ct)=>CompleteAsync(messages,purpose,ct,true);
    private Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,GenerationPurpose purpose,CancellationToken ct,bool useAdapter)=>purpose switch
    {
        GenerationPurpose.Sketch => RequestAsync(messages,true,.65,false,ct,SketchSchema,1350),
        GenerationPurpose.RequestedSketch => RequestAsync(messages,true,.25,false,ct,SketchSchema,3000),
        GenerationPurpose.SketchRevision => RequestAsync(messages,true,.2,false,ct,SketchSchema,3600,thinkingBudget:512),
        GenerationPurpose.SketchInspection => RequestAsync(messages,false,0,false,ct,maxTokens:140),
        GenerationPurpose.SketchReview => RequestAsync(messages,true,0,false,ct,SketchReviewSchema,240),
        GenerationPurpose.WorkSearch => RequestAsync(messages,false,0,false,ct,maxTokens:100),
        GenerationPurpose.PersonalThought => RequestAsync(messages,false,.58,false,ct,maxTokens:480),
        GenerationPurpose.PersonalOutline => RequestAsync(messages,true,.64,false,ct,PersonalOutlineSchema,650),
        GenerationPurpose.PersonalScene => RequestAsync(messages,false,.66,false,ct,maxTokens:640),
        GenerationPurpose.PersonalReview => RequestAsync(messages,true,.20,false,ct,PersonalReviewSchema,260),
        GenerationPurpose.ScreenRelevance => RequestAsync(messages,true,0,false,ct,ScreenRelevanceSchema,48),
        GenerationPurpose.WorkTask => RequestAsync(messages,true,0,false,ct,WorkTaskSchema,1000),
        GenerationPurpose.StandardDialogue => RequestAsync(messages,false,.6,true,ct,maxTokens:Math.Min(1200,options.MaxReplyTokens+240),thinkingBudget:192,useAdapter:useAdapter),
        GenerationPurpose.DeepDialogue => RequestAsync(messages,false,.6,true,ct,maxTokens:Math.Min(2000,options.MaxReplyTokens+800),thinkingBudget:640,useAdapter:useAdapter),
        GenerationPurpose.WorkStep => RequestAsync(messages,true,.15,false,ct,WorkStepSchema,1000),
        GenerationPurpose.DeepWorkStep => RequestAsync(messages,true,.15,false,ct,WorkStepSchema,1700,thinkingBudget:640),
        _ => RequestAsync(messages,purpose is GenerationPurpose.Metadata or GenerationPurpose.ToolPlanning,purpose==GenerationPurpose.Dialogue?options.Temperature:0.25,purpose==GenerationPurpose.Dialogue,ct,useAdapter:useAdapter)
    };
    public Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages,CancellationToken ct)=>RequestAsync(messages,true,0,false,ct,ContextSchema,360);
    Task<string> IPublicationIntentModel.InterpretPublicationAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
        RequestAsync(messages, true, 0, false, ct, PublicationIntentSchema, 260);
    Task<string> IReplyTaskModel.ReviewTaskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
        RequestAsync(messages, true, 0, false, ct, ReplyTaskSchema, 650);
    public async Task<string?> PlanSpeechAsync(IReadOnlyList<ChatMessage> messages,int sentenceCount,CancellationToken ct)=>
        await RequestAsync(messages,true,0,false,ct,SpeechSchemas[sentenceCount-1],140);
    private async Task<string> RequestAsync(IReadOnlyList<ChatMessage> messages,bool json,double temperature,bool dialogue,CancellationToken ct,JsonElement? schema=null,int? maxTokens=null,int thinkingBudget=0,bool useAdapter=false)
    {
        messages = await FitContextAsync(messages,maxTokens??(json ? options.MaxReplyTokens : 1800), ct);
        var converted = messages.Select(m => new Dictionary<string,object?> { ["role"] = m.Role, ["content"] = m.Image is null ? m.Text : new object[] { new { type="text", text=m.Text }, new { type="image_url", image_url=new { url="data:" + (m.ImageMediaType == "image/jpeg" ? "image/jpeg" : "image/png") + ";base64," + Convert.ToBase64String(m.Image) } } } }).ToArray();
        var body = new Dictionary<string, object?> { ["model"] = "neighbour", ["messages"] = converted, ["temperature"] = temperature, ["max_tokens"] = maxTokens??(json||dialogue ? options.MaxReplyTokens : 1800), ["stream"] = false, ["chat_template_kwargs"] = new { enable_thinking = thinkingBudget>0 } };
        if(Volatile.Read(ref incognito)!=0)body["cache_prompt"]=false;
        if(dialogue&&useAdapter&&LoadedAdapter is not null)body["lora"]=new[] {new {id=0,scale=1.0}};
        if(thinkingBudget>0)body["thinking_budget_tokens"]=thinkingBudget;
        if(dialogue){body["repeat_penalty"]=1.1;body["repeat_last_n"]=256;body["top_p"]=0.9;}
        if (json) body["response_format"] = new { type = "json_object", schema=schema??DecisionSchema };
        using var response = await client.PostAsJsonAsync("v1/chat/completions", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Model HTTP {(int)response.StatusCode}: {error[..Math.Min(500,error.Length)]}");
        }
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var choice = doc.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length") throw new InvalidDataException("Model reply was truncated; increase maxReplyTokens or request a shorter answer.");
        var content=choice.GetProperty("message").GetProperty("content").GetString() ?? throw new InvalidDataException("Model returned empty content.");
        if(thinkingBudget==0)return content;
        // An externally configured local server can still put thought tags in content.
        var end=content.LastIndexOf("</think>",StringComparison.OrdinalIgnoreCase);
        if(end>=0)content=content[(end+"</think>".Length)..].TrimStart();
        if(content.Contains("<think",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model did not finish its final response.");
        return content;
    }
    private async Task<IReadOnlyList<ChatMessage>> FitContextAsync(IReadOnlyList<ChatMessage> source,int replyTokens,CancellationToken ct)
    {
        var selected=source.ToList();
        for(var attempt=0;attempt<100;attempt++)
        {
            // Count with the model's own tokenizer, with room for role/template and vision tokens.
            using var response=await client.PostAsJsonAsync("tokenize",new { content=string.Join("\n",selected.Select(m=>m.Text)),add_special=true,parse_special=false },ct);
            response.EnsureSuccessStatusCode();
            using var tokens=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);
            var used=tokens.RootElement.GetProperty("tokens").GetArrayLength()+selected.Count*64+selected.Count(m=>m.Image is not null)*1200;
            if(used+replyTokens+256<=options.ContextSize)return selected;
            var history=selected.FindIndex(m=>m.IsHistory);
            if(history>=0) {selected.RemoveAt(history);continue;}
            // Preserve instructions and the most recent request. Clip large external tool payloads first.
            var longest=selected.Select((m,i)=>(m,i)).Where(x=>x.i>0 && x.m.Text.Length>4000).OrderByDescending(x=>x.m.Text.Length).FirstOrDefault();
            if(longest.m is null)throw new InvalidDataException("Prompt exceeds model context. Shorten the character file/request or increase model.contextSize.");
            var keep=Math.Max(2000,longest.m.Text.Length/2);
            selected[longest.i]=longest.m with { Text=longest.m.Text[..keep]+"\n[Content clipped to fit model context; do not claim to have read omitted sections.]" };
        }
        throw new InvalidDataException("Unable to fit the conversation into the model context.");
    }
    private async Task StopAsync()
    {
        if (process is null) return;
        using (process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync();
            if (drainTask is not null) await drainTask;
        }
        process = null;
        drainTask = null;
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { client.Dispose(); }
    }
}
