using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Inference;

public sealed record ChatMessage(string Role, string Text, byte[]? Image = null, bool IsHistory = false);
public enum GenerationPurpose { Dialogue, Summary, Metadata, ToolPlanning, Sketch, WorkTask }
public interface IChatModel
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct);
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, GenerationPurpose purpose, CancellationToken ct)=>CompleteAsync(messages,purpose is GenerationPurpose.Metadata or GenerationPurpose.ToolPlanning or GenerationPurpose.Sketch or GenerationPurpose.WorkTask,ct);
}

public interface ITurnContextModel
{
    Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages,CancellationToken ct);
    Task<string?> PlanSpeechAsync(IReadOnlyList<ChatMessage> messages,int sentenceCount,CancellationToken ct)=>Task.FromResult<string?>(null);
}

public sealed class LocalModel(PortablePaths paths, ModelOptions options) : IChatModel, ITurnContextModel, IAsyncDisposable
{
    private static readonly JsonElement ContextSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["intent","request","uncertainty","evidence"],"properties":{
      "intent":{"type":"string","enum":["chat","screen","screen_followup","file_capability","web_capability","lookup_term"]},
      "request":{"type":"string","maxLength":450},"uncertainty":{"type":"string","maxLength":180},"evidence":{"type":"string","maxLength":120}
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
      "shapes":{"type":"array","minItems":3,"maxItems":24,"items":{"type":"object","additionalProperties":false,
        "required":["kind","color","x","y","w","h"],"properties":{
          "kind":{"type":"string","enum":["rectangle","ellipse","triangle"]},"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"},
          "x":{"type":"integer","minimum":0,"maximum":99},"y":{"type":"integer","minimum":0,"maximum":99},
          "w":{"type":"integer","minimum":1,"maximum":100},"h":{"type":"integer","minimum":1,"maximum":100}
        }}}
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
    private static readonly JsonElement DecisionSchema=JsonSerializer.Deserialize<JsonElement>("""
    {"type":"object","additionalProperties":false,"required":["growth","speak","reply","memory","emotion","relationship","actions"],"properties":{
      "growth":{"anyOf":[{"type":"object","additionalProperties":false,"required":["action","kind","name","expression","evidence"],"properties":{
        "action":{"type":"string","enum":["explore","reinforce","reconsider"]},"kind":{"type":"string","enum":["habit","interest"]},
        "name":{"type":"string","maxLength":64},"expression":{"type":"string","maxLength":160},"evidence":{"type":"string","maxLength":120}
      }},{"type":"null"}]},
      "speak":{"type":"boolean"},"reply":{"type":"string","maxLength":4000},"memory":{"type":"string","maxLength":1000},
      "emotion":{"type":"object","additionalProperties":false,"required":["warmth","trust","closeness","valence","energy","stress","curiosity","reason"],"properties":{
        "warmth":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"trust":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"closeness":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},
        "valence":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"energy":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"stress":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"curiosity":{"type":"number","enum":[-0.03,-0.02,-0.01,0,0.01,0.02,0.03]},"reason":{"type":"string","maxLength":160}}},
      "relationship":{"type":"object","additionalProperties":false,"required":["currentAttitude","recentImpression","appreciates","concerns","boundaries"],"properties":{"currentAttitude":{"type":["string","null"],"maxLength":500},"recentImpression":{"type":["string","null"],"maxLength":300},"appreciates":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}},"concerns":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}},"boundaries":{"type":["array","null"],"maxItems":5,"items":{"type":"string","maxLength":160}}}},
      "actions":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,"required":["tool","args"],"properties":{"tool":{"type":"string","maxLength":80},"args":{"type":"object"}}}}
    }}
    """);
    private readonly HttpClient client = new() { BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
    private Process? process;
    private Task? drainTask;
    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.AutoStart) { await WaitReadyAsync(ct); return; }
        if (await HealthyAsync(ct)) throw new IOException("Model port is already occupied. Stop the existing server, use another endpoint, or explicitly set model.autoStart=false.");
        foreach (var file in new[] { options.Executable, options.ModelFile, options.ProjectorFile })
            if (!File.Exists(paths.Inside(file))) throw new FileNotFoundException("Local dependency missing. Run Setup.cmd first.", file);
        var info = new ProcessStartInfo(paths.Inside(options.Executable)) { WorkingDirectory = paths.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var endpoint = new Uri(options.Endpoint);
        string[] arguments = ["--model", paths.Inside(options.ModelFile), "--mmproj", paths.Inside(options.ProjectorFile), "--host", "127.0.0.1", "--port", endpoint.Port.ToString(), "--ctx-size", options.ContextSize.ToString(), "--n-gpu-layers", options.GpuLayers.ToString(), "--threads", options.Threads.ToString(), "--parallel", "1", "--batch-size", "512", "--ubatch-size", "128", "--image-max-tokens", "1024", "--no-webui", "--offline", "--jinja", "--alias", "neighbour"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--poll", "0", "--poll-batch", "0", "--image-min-tokens", "1024", "--cache-ram", "0", "--cors-origins", "http://127.0.0.1" }) info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new IOException("Cannot start llama-server.");
        ChildProcessJob.Attach(process);
        drainTask = Task.WhenAll(DrainAsync(process.StandardOutput, "llama.stdout.log"), DrainAsync(process.StandardError, "llama.stderr.log"));
        await WaitReadyAsync(ct);
    }
    private async Task DrainAsync(StreamReader reader, string filename)
    {
        try
        {
            var file = paths.Inside("data/logs/" + filename);
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".previous", true);
            await using var writer = new StreamWriter(file, append: true) { AutoFlush = true };
            long written = 0;
            while (await reader.ReadLineAsync() is { } line) { if (written < 4_000_000) { await writer.WriteLineAsync(line); written += line.Length; } }
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
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,GenerationPurpose purpose,CancellationToken ct)=>purpose switch
    {
        GenerationPurpose.Sketch => RequestAsync(messages,true,.65,false,ct,SketchSchema,1350),
        GenerationPurpose.WorkTask => RequestAsync(messages,true,0,false,ct,WorkTaskSchema,1000),
        _ => RequestAsync(messages,purpose is GenerationPurpose.Metadata or GenerationPurpose.ToolPlanning,purpose==GenerationPurpose.Dialogue?options.Temperature:0.25,purpose==GenerationPurpose.Dialogue,ct)
    };
    public Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages,CancellationToken ct)=>RequestAsync(messages,true,0,false,ct,ContextSchema,320);
    public async Task<string?> PlanSpeechAsync(IReadOnlyList<ChatMessage> messages,int sentenceCount,CancellationToken ct)=>
        await RequestAsync(messages,true,0,false,ct,SpeechSchemas[sentenceCount-1],140);
    private async Task<string> RequestAsync(IReadOnlyList<ChatMessage> messages,bool json,double temperature,bool dialogue,CancellationToken ct,JsonElement? schema=null,int? maxTokens=null)
    {
        messages = await FitContextAsync(messages,maxTokens??(json ? options.MaxReplyTokens : 1800), ct);
        var converted = messages.Select(m => new Dictionary<string,object?> { ["role"] = m.Role, ["content"] = m.Image is null ? m.Text : new object[] { new { type="text", text=m.Text }, new { type="image_url", image_url=new { url="data:image/png;base64," + Convert.ToBase64String(m.Image) } } } }).ToArray();
        var body = new Dictionary<string, object?> { ["model"] = "neighbour", ["messages"] = converted, ["temperature"] = temperature, ["max_tokens"] = maxTokens??(json||dialogue ? options.MaxReplyTokens : 1800), ["stream"] = false, ["chat_template_kwargs"] = new { enable_thinking = false } };
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
        return choice.GetProperty("message").GetProperty("content").GetString() ?? throw new InvalidDataException("Model returned empty content.");
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
    public async ValueTask DisposeAsync()
    {
        if (process is not null) { try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { } process.Dispose(); }
        if (drainTask is not null) await drainTask;
        client.Dispose();
    }
}
