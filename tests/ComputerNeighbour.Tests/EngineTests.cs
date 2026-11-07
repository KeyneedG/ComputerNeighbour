using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class EngineTests
{
    private static string Decision(string reply = "", List<ToolRequest>? actions = null, bool speak = true, string memory = "") =>
        JsonSerializer.Serialize(new CompanionDecision { Speak = speak, Reply = reply, Memory = memory, Emotion = new EmotionDelta(), Actions = actions ?? [] }, AppConfig.Json);
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("explicit read requests are grounded in actual article text before the first model call", async root =>
        {
            var model = new ScriptedModel(
                "The article explains portable solar charging.");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "SOLAR_ARTICLE_SENTINEL: portable panels charge a battery during daylight.");
            await fixture.Engine.RespondAsync("Прочитай documents/article.md и расскажи о чём статья.", "alex", false, false, CancellationToken.None);
            Check.Equal(1, model.Calls.Count);
            Check.True(model.Calls[0].Messages.Any(m => m.Text.Contains("SOURCE:") && m.Text.Contains("SOLAR_ARTICLE_SENTINEL")), "The first model call did not receive the actual article result.");
            Check.True(fixture.Output.Lines.Any(s => s.EndsWith("The article explains portable solar charging.")), "The final English reply was not printed.");
            Check.True(fixture.Memory.Recent.Any(e => e.Role == "assistant" && e.Text == "The article explains portable solar charging."), "The final reply was not persisted.");
            Check.True(fixture.Memory.Recent.Any(e => e.Role == "user" && e.SpeakerId == "alex"), "The speaker identity was lost in the engine.");
            Check.True(model.Calls.All(c => !c.Json && c.Messages.All(m => m.Image is null)), "A direct source-summary turn captured an image or requested structured dialogue output.");
        });
        await suite.Case("model-initiated reads still support a real file tool round trip for requests outside the explicit router", async root =>
        {
            var model = new ScriptedModel(
                Decision(actions: [new("read_file", Args(new { path = "documents/article.md" }))]),
                Decision("The article explains portable solar charging."));
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "MODEL_READ_SENTINEL: solar panels charge a battery.");
            await fixture.Engine.RespondAsync("Would you inspect documents/article.md for me?", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls[1].Messages.Any(m => m.Text.Contains("TOOL RESULTS") && m.Text.Contains("MODEL_READ_SENTINEL")), "The model-initiated tool result was lost.");
            Check.True(fixture.Output.Lines.Any(l => l.EndsWith("The article explains portable solar charging.")), "The tool-assisted answer was not printed.");
        });
        await suite.Case("direct plaintext summaries retry Russian output before printing and saving English facts", async root =>
        {
            var model = new ScriptedModel("В статье рассказано о зарядке аккумулятора солнечной панелью.", "A solar panel charges the battery during daylight.");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "Солнечная панель заряжает аккумулятор днём.");
            await fixture.Engine.RespondAsync("Прочитай documents/article.md.", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls.All(c => !c.Json), "A plaintext summary retry switched into structured dialogue mode.");
            Check.True(fixture.Output.Lines.Any(l => l.EndsWith("A solar panel charges the battery during daylight.")), "The English plaintext retry was not printed.");
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("В статье рассказано")), "The rejected Russian summary was printed.");
            Check.True(fixture.Memory.Recent.Where(e => e.Role is "assistant" or "note").All(e => !e.Text.Contains("В статье рассказано")), "The rejected Russian summary was persisted.");
        });
        await suite.Case("two empty plaintext summaries report the actual read without saving an invented factual note", async root =>
        {
            var model = new ScriptedModel("", " \n\t");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "A readable source whose failed summary must not be fabricated.");
            await fixture.Engine.RespondAsync("Read documents/article.md.", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls.All(c => !c.Json), "The direct summary unexpectedly requested structured output.");
            Check.True(fixture.Output.Lines.Last().Contains("I read the file, but couldn't"),"The file read was lost when the summary failed.");
            Check.True(fixture.Memory.Recent.All(e => e.Role != "note"), "A failed summary committed a factual note.");
            Check.Equal("", fixture.Memory.Summary);
        });
        await suite.Case("context classification precedes image capture and screen follow-ups use real evidence",async root=>
        {
            var model=new ContextModel("""{"intent":"screen"}""","You are looking at a railway scene in Unity.");
            var captures=0;
            Fixture? fixture=null;
            fixture=await Fixture.CreateAsync(root,model,capture:()=>
            {
                Check.True(fixture!.Output.Lines.Last().Contains("I'll take a look"),"The screen was captured before the notice.");
                Check.Equal(1,model.Classifications.Count,"Intent was not understood before capture.");
                captures++;
                return [1,2,3];
            });
            await using var f=fixture;
            f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
            await f.Engine.RespondAsync("Что видно в этом окне сейчас?","user",false,false,default);
            Check.Equal(1,captures);
            Check.True(model.Answers.Single().Any(m=>m.Image is [1,2,3]),"The actual captured image did not reach the answer stage.");
            await f.Engine.RespondAsync("Так что ты все таки можешь смотреть что я делаю :/","user",false,false,default);
            await f.Engine.RespondAsync("Так ты действительно видела мой экран и точно описала что на нем происходило","user",false,false,default);
            Check.Equal(1,captures);
            Check.True(f.Output.Lines.Last().Contains("I took a real screenshot"),"The confirmed previous image was denied or forgotten.");
            Check.True(!f.Memory.Recent.Any(e=>e.Text.Contains("I'll take a look")),"The action notice was saved as a finished answer.");
        });
        await suite.Case("natural screen request handles disabled, paused and failed capture without guessing",async root=>
        {
            var calls=0;
            var model=new ScriptedModel();
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>{calls++;throw new IOException("Test desktop unavailable.");});
            const string request="Можешь сказать чем я сейчас занят? (Подсказка, посмиотри экран)";
            await f.Engine.RespondAsync(request,"user",false,false,default);
            f.Config.Permissions.ScreenCapture=true;
            await f.Engine.RespondAsync(request,"user",false,false,default);
            Check.Equal(0,calls);
            f.Engine.Paused=false;
            await f.Engine.RespondAsync(request,"user",false,false,default);
            Check.Equal(1,calls);
            Check.True(f.Output.Lines.Last().Contains("Test desktop unavailable"),"Capture failure was hidden.");
            await f.Engine.RespondAsync("Так ты действительно видела мой экран?","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("haven't completed"),"A failed capture became verified evidence.");
            Check.Equal(0,model.Calls.Count);
        });
        await suite.Case("a question about a visible photo gets an opinion using the character's own voice",async root=>
        {
            const string opinion="That window light makes the empty chair feel like it has its own agenda.";
            var model=new ScriptedModel(opinion+" I'd keep that lighting.\n\nVisible details: a window, a chair, and a floor.");
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>[1,2,3]);
            f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
            await f.Engine.RespondAsync("Что думаешь об этой фотографии?","user",false,false,default);
            var messages=model.Calls.Single().Messages;
            Check.True(messages[0].Text.Contains("calm, curious fictional neighbour")&&messages[0].Text.Contains("Evolving habits and interests"),"Visual replies did not use the character's voice.");
            Check.True(messages[0].Text.Contains("give your own take")&&messages.Last().Image is [1,2,3],"The opinion request was not grounded in the actual image.");
            Check.True(f.Output.Lines.Last().EndsWith(opinion+" I'd keep that lighting."),"The visual reaction did not stop after the personal thought.");
        });
        await suite.Case("autonomous screen glances drop captions and repeated commentary while keeping a personal aside",async root=>
        {
            const string aside="That chair has the dramatic lighting of a villain about to explain their plan.";
            var model=new ScriptedModel(Decision("The photo shows a chair beside a window.",memory:"Do not save this rejected caption."),
                Decision(aside),Decision(aside));
            byte capture=0;
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>[1,2,++capture]);
            f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;f.Config.Observation.ScreenIntervalSeconds=0;
            await f.Engine.RespondAsync("Scheduled opportunity.","environment",true,false,default);
            Check.Equal(0,f.Output.Lines.Count);
            Check.Equal(0,f.Memory.Recent.Count);
            var first=model.Calls[0].Messages;
            Check.True(first[0].Text.Contains(@"""speak"":false")&&first.Last().Text.Contains("Start from silence")&&first.Last().Image is [1,2,1],"An autonomous glance asked for a caption or missed the screenshot.");
            await f.Engine.RespondAsync("Scheduled opportunity.","environment",true,false,default);
            await f.Engine.RespondAsync("Scheduled opportunity.","environment",true,false,default);
            Check.Equal(1,f.Output.Lines.Count);
            Check.True(f.Output.Lines.Single().EndsWith(aside),"The personal observation was filtered out.");
            Check.Equal(1,f.Engine.UnansweredMessages);
        });
        await suite.Case("reads live Notepad text for reported filename and reports actual file capability",async root=>
        {
            var open=new OpenDocuments(new("LIVE_UNSAVED_SENTINEL: purple trains.",null));
            var model=new ScriptedModel("Your open note describes purple trains.");
            await using var f=await Fixture.CreateAsync(root,model,openDocuments:open);
            Directory.CreateDirectory(Path.Combine(root,"documents"));
            await File.WriteAllTextAsync(Path.Combine(root,"documents","PVP1.txt"),"STALE_DISK_SENTINEL");
            await f.Engine.RespondAsync("Are you able to read texts on this PC?","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("open Notepad tab"),"The actual file capability was denied.");
            await f.Engine.RespondAsync("прочти текст в открытом блокноте PVP1.txt","user",false,false,default);
            Check.Equal(("PVP1.txt",true),open.Requests.Single());
            var source=model.Calls.Single().Messages.Last().Text;
            Check.True(source.Contains("LIVE_UNSAVED_SENTINEL")&&!source.Contains("STALE_DISK_SENTINEL"),"Disk text replaced the live Notepad text.");
            Check.True(f.Output.Lines[^2].Contains("I'll check the open Notepad tabs"),"The Notepad action was not announced.");
        });
        await suite.Case("filename search uses configured folders and reports duplicate files",async root=>
        {
            var open=new OpenDocuments(null);
            var model=new ScriptedModel("The file discusses railway signals.");
            await using var f=await Fixture.CreateAsync(root,model,openDocuments:open);
            var nested=Path.Combine(root,"documents","subfolder");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested,"notes.TXT"),"RAILWAY_SIGNAL_SENTINEL");
            await f.Engine.RespondAsync("Найди файл notes.txt","user",false,false,default);
            Check.Equal(("notes.txt",false),open.Requests.Single());
            Check.True(f.Output.Lines.Last().Contains("subfolder"),"Nested filename was not found.");
            await f.Engine.RespondAsync("Прочитай notes.txt","user",false,false,default);
            Check.True(model.Calls.Single().Messages.Last().Text.Contains("RAILWAY_SIGNAL_SENTINEL"),"File search did not read its match.");
            await File.WriteAllTextAsync(Path.Combine(root,"documents","notes.txt"),"OTHER_SENTINEL");
            await f.Engine.RespondAsync("Прочитай notes.txt","user",false,false,default);
            Check.Equal(1,model.Calls.Count);
            Check.True(f.Output.Lines.Last().Contains("More than one file matches"),"An ambiguous filename was read.");
        });
        await suite.Case("unavailable open document cannot silently fall back to an outdated disk copy",async root=>
        {
            var open=new OpenDocuments(new(null,"Select the matching Notepad tab."));
            var model=new ScriptedModel();
            await using var f=await Fixture.CreateAsync(root,model,openDocuments:open);
            Directory.CreateDirectory(Path.Combine(root,"documents"));
            await File.WriteAllTextAsync(Path.Combine(root,"documents","PVP1.txt"),"Disk copy.");
            await f.Engine.RespondAsync("Read PVP1.txt","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("Select the matching Notepad tab"),"The live-read limitation was lost.");
            Check.Equal(0,model.Calls.Count);
            f.Config.Permissions.ReadFiles=false;
            await f.Engine.RespondAsync("Read PVP1.txt","user",false,false,default);
            Check.Equal(1,open.Requests.Count);
            Check.True(f.Output.Lines.Last().Contains("File reading is disabled"),"Disabled file permissions were bypassed.");
        });
        await suite.Case("autonomous model decisions cannot execute requested tools or generate approval prompts", async root =>
        {
            var model = new ScriptedModel(Decision(actions: [new("read_file", Args(new { path = "documents/should-not-be-read.md" })), new("open_app", Args(new { name = "never" }))], speak: false));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("Scheduled opportunity.", "environment", true, false, CancellationToken.None);
            Check.Equal(1, model.Calls.Count);
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("/approve") || l.Contains("[Tool")), "An autonomous decision attempted a computer action.");
            Check.Equal(0, fixture.Memory.Recent.Count);
            Check.True(!model.Calls.SelectMany(c => c.Messages).Any(m => m.Text.Contains("TOOL RESULTS")), "An autonomous tool result reached the model.");
        });
        await suite.Case("explicit application requests are queued honestly without calling the model or starting an executable", async root =>
        {
            var model = new ScriptedModel();
            await using var fixture = await Fixture.CreateAsync(root, model);
            fixture.Config.Permissions.AllowedApplications["never"] = "documents/this-executable-does-not-exist.exe";
            await fixture.Engine.RespondAsync("Open never.", "user", false, false, CancellationToken.None);
            Check.Equal(0, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("[Approval 1]") && l.Contains("/approve 1")), "The user was not given an explicit approval request.");
            Check.True(fixture.Output.Lines.Any(l => l.Contains("I've prepared that action. Please approve request 1")), "The pending action was not described honestly.");
            fixture.Engine.Reject(1);
            Check.True(fixture.Output.Lines.Contains("Action rejected."), "The pending action could not be rejected.");
        });
        await suite.Case("model-initiated application requests still require approval for wording outside the explicit router", async root =>
        {
            var model = new ScriptedModel(
                Decision(actions: [new("open_app", Args(new { name = "never" }))]),
                Decision("Please approve the pending application request."));
            await using var fixture = await Fixture.CreateAsync(root, model);
            fixture.Config.Permissions.AllowedApplications["never"] = "documents/this-executable-does-not-exist.exe";
            await fixture.Engine.RespondAsync("I would like the never application running.", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("[Approval 1]") && l.Contains("/approve 1")), "The model-initiated action bypassed approval.");
            Check.True(model.Calls[1].Messages.Any(m => m.Text.Contains("TOOL RESULTS") && m.Text.Contains("Pending approval 1; not executed.")), "The model did not receive an honest pending result.");
        });
        await suite.Case("malformed and incomplete decision JSON cannot authorize tools or persist fabricated facts", async root =>
        {
            var model = new ScriptedModel(
                "{\"speak\":true,\"reply\":\"\",\"memory\":\"fabricated\",\"emotion\":{},\"actions\":[{\"tool\":\"open_app\",\"args\":{\"name\":\"notepad\"}}]",
                "{\"reply\":\"Incomplete schema\",\"actions\":[{\"tool\":\"open_app\",\"args\":{\"name\":\"notepad\"}}]}");
            await using var fixture = await Fixture.CreateAsync(root, model);
            var rejected = false;
            try { await fixture.Engine.RespondAsync("Would you inspect documents/example.md?", "user", false, false, CancellationToken.None); }
            catch (Exception e) when (e is JsonException or InvalidDataException) { rejected = true; }
            Check.True(rejected, "An incomplete or malformed decision was accepted.");
            Check.Equal(2, model.Calls.Count);
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("/approve") || l.Contains("[Tool")), "An invalid decision caused an action.");
            Check.True(fixture.Memory.Recent.All(e => e.Role == "user"), "An invalid decision persisted model-generated memory or a reply.");
        });
        await suite.Case("Russian model output is retried before an English response is printed", async root =>
        {
            var model = new ScriptedModel("Это ответ на русском языке.", "I understand Russian and will answer in English.", Decision(speak:false));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("Ты понимаешь русский?", "user", false, false, CancellationToken.None);
            Check.Equal(3, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("I understand Russian and will answer in English.")), "The valid English retry was not printed.");
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("Это ответ")), "Rejected Russian output was printed.");
        });
    }

    private sealed class ScriptedModel(params string[] responses) : IChatModel
    {
        public List<ModelCall> Calls { get; } = [];
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new(messages.ToArray(), json));
            if (Calls.Count > responses.Length) throw new InvalidOperationException("The engine requested an unexpected extra model response.");
            return Task.FromResult(responses[Calls.Count - 1]);
        }
    }
    private sealed record ModelCall(ChatMessage[] Messages, bool Json);
    private sealed class ContextModel(string intent,string answer):IChatModel,ITurnContextModel
    {
        public List<IReadOnlyList<ChatMessage>> Classifications {get;}=[];
        public List<IReadOnlyList<ChatMessage>> Answers {get;}=[];
        public Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages,CancellationToken ct){Classifications.Add(messages.ToArray());return Task.FromResult(intent);}
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,bool json,CancellationToken ct){Answers.Add(messages.ToArray());return Task.FromResult(answer);}
    }
    private sealed class OpenDocuments(OpenTextDocument? document):IOpenDocumentReader
    {
        public List<(string Name,bool Read)> Requests {get;}=[];
        public Task<OpenTextDocument?> FindAsync(string fileName,bool read,CancellationToken ct){Requests.Add((fileName,read));return Task.FromResult(document);}
    }
    private sealed class CapturingOutput : ICompanionOutput
    {
        public List<string> Lines { get; } = [];
        public void Write(string text) => Lines.Add(text);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required AppConfig Config { get; init; }
        public required MemoryStore Memory { get; init; }
        public required CompanionEngine Engine { get; init; }
        public required AudioService Audio { get; init; }
        public required WeatherService Weather { get; init; }
        public required CapturingOutput Output { get; init; }
        public static async Task<Fixture> CreateAsync(string root, IChatModel model,IOpenDocumentReader? openDocuments=null,Func<byte[]>? capture=null)
        {
            var paths = new PortablePaths(root);
            var config = new AppConfig();
            config.Audio.Enabled = false;
            config.Permissions.Microphone = false;
            config.Permissions.ScreenCapture = false;
            config.Permissions.ForegroundWindow = false;
            config.Permissions.WeatherNetwork = false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile), "You are a calm, curious fictional neighbour. Respond in natural English.");
            var memory = new MemoryStore(root, config.Memory); await memory.InitializeAsync();
            var emotion = new EmotionEngine(root); await emotion.InitializeAsync();
            var audio = new AudioService(root, config.Audio);
            var weather = new WeatherService(config);
            var output = new CapturingOutput();
            return new Fixture
            {
                Config = config, Memory = memory, Audio = audio, Weather = weather, Output = output,
                Engine = new CompanionEngine(paths, config, model, memory, emotion, new WindowsDesktop(config.Desktop), audio, output, weather,openDocuments:openDocuments,captureScreen:capture) { Paused = true }
            };
        }
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Audio.DisposeAsync();
            Weather.Dispose();
        }
    }
}
