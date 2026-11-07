using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class FileEngineTests
{
    private static Dictionary<string,JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(value))!;
    private static string Decision(params ToolRequest[] requests) => JsonSerializer.Serialize(new CompanionDecision { Speak=false, Reply="", Memory="", Emotion=new(), Actions=requests.ToList() },AppConfig.Json);

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("explicit literal file requests stage concrete contents and write only after approval", async root =>
        {
            var model=new ScriptedModel();await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("write documents/note.txt |hello\nsecond line","user",false,false,CancellationToken.None);
            var target=Path.Combine(root,"documents","note.txt");
            Check.True(!File.Exists(target),"An unapproved file was written.");
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("[Approval 1]")&&l.Contains("+hello")&&l.Contains("Full proposed contents:")),"The actual file preview was not shown.");
            var proposals=Directory.GetFiles(Path.Combine(root,"data","file-proposals"),"*.json");Check.Equal(1,proposals.Length);
            var proposal=JsonSerializer.Deserialize<PreparedFileChange>(await File.ReadAllTextAsync(proposals[0]),AppConfig.Json)!;
            Check.Equal("documents/note.txt",proposal.RelativePath);Check.Equal("hello\nsecond line",proposal.AfterText);
            Check.Equal(0,model.Calls);
            await fixture.Engine.ApproveAsync(1,CancellationToken.None);
            Check.Equal("hello\nsecond line",await File.ReadAllTextAsync(target));Check.Equal(0,model.Calls);
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("Created documents/note.txt.")),"The completed write was not reported.");
        });
        await suite.Case("rejected file proposals never write or become applicable through a later approval", async root =>
        {
            var model=new ScriptedModel();await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("create documents/rejected.md |This must stay a proposal.","user",false,false,CancellationToken.None);
            fixture.Engine.Reject(1);await fixture.Engine.ApproveAsync(1,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","rejected.md")),"A rejected proposal wrote a target.");
            Check.True(fixture.Output.Lines.Contains("Action rejected.")&&fixture.Output.Lines.Contains("No such pending action."),"A rejection did not invalidate the pending action.");
            Check.Equal(0,model.Calls);
        });
        await suite.Case("approved edits retain the reviewed snapshot and reject externally changed files", async root =>
        {
            var model=new ScriptedModel();await using var fixture=await Fixture.CreateAsync(root,model);
            Directory.CreateDirectory(Path.Combine(root,"documents"));var target=Path.Combine(root,"documents","note.md");
            const string original="First\nReplace me\nLast\n";const string newer=original+"External update\n";
            await File.WriteAllTextAsync(target,original);
            await fixture.Engine.RespondAsync("edit documents/note.md |Replace me|Replacement","user",false,false,CancellationToken.None);
            Check.Equal(original,await File.ReadAllTextAsync(target));
            await File.WriteAllTextAsync(target,newer);
            await fixture.Engine.ApproveAsync(1,CancellationToken.None);
            Check.Equal(newer,await File.ReadAllTextAsync(target));Check.Equal(0,model.Calls);
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("changed after this proposal")&&l.Contains("nothing was overwritten",StringComparison.OrdinalIgnoreCase)),"The stale reviewed snapshot was not reported as a conflict.");
        });
        await suite.Case("autonomous file proposals require approval without full access even when general confirmations are disabled", async root =>
        {
            var model=new ScriptedModel(Decision(new ToolRequest("CREATE_FILE",Args(new {path="documents/idea.md",content="A small shared project idea."}))));
            await using var fixture=await Fixture.CreateAsync(root,model,c=>{c.Permissions.ConfirmActions=false;c.Permissions.ConfirmFileChanges=false;c.Permissions.FullFileAccess=false;});
            await fixture.Engine.RespondAsync("A useful follow-up to our earlier project discussion.","environment",true,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","idea.md")),"An autonomous file action bypassed the full-access policy.");
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("[Approval 1")&&l.Contains("character initiative")&&l.Contains("+A small shared project idea.")),"An autonomous change was not identified and previewed.");
            Check.Equal(1,model.Calls);
            await fixture.Engine.ApproveAsync(1,CancellationToken.None);
            Check.Equal("A small shared project idea.",await File.ReadAllTextAsync(Path.Combine(root,"documents","idea.md")));
            Check.Equal(1,model.Calls);
        });
        await suite.Case("full file access applies autonomous creates and edits immediately with original-byte backups", async root =>
        {
            var model=new ScriptedModel(
                Decision(new ToolRequest("create_file",Args(new {path="documents/idea.md",content="first version"}))),
                Decision(new ToolRequest("edit_file",Args(new {path="documents/idea.md",oldText="first",newText="second"}))));
            await using var fixture=await Fixture.CreateAsync(root,model,c=>{c.Permissions.FullFileAccess=true;c.Permissions.ConfirmActions=true;c.Permissions.ConfirmFileChanges=true;});
            await fixture.Engine.RespondAsync("An opportunity to write a useful note.","environment",true,false,CancellationToken.None);
            var target=Path.Combine(root,"documents","idea.md");Check.Equal("first version",await File.ReadAllTextAsync(target));
            await fixture.Engine.RespondAsync("A later opportunity to update the same note.","environment",true,false,CancellationToken.None);
            Check.Equal("second version",await File.ReadAllTextAsync(target));
            var backup=Directory.GetFiles(Path.Combine(root,"data","file-backups"),"before.bin",SearchOption.AllDirectories).Single();
            Check.Equal("first version",await File.ReadAllTextAsync(backup));
            Check.True(!fixture.Output.Lines.Any(l=>l.Contains("[Approval")),"Explicit full file access still generated file approval requests.");
            Check.Equal(2,model.Calls);
        });
        await suite.Case("full file access applies explicit literal writes without a model call or file approval", async root =>
        {
            var model=new ScriptedModel();await using var fixture=await Fixture.CreateAsync(root,model,c=>c.Permissions.FullFileAccess=true);
            await fixture.Engine.RespondAsync("write documents/direct.txt |direct contents","user",false,false,CancellationToken.None);
            Check.Equal("direct contents",await File.ReadAllTextAsync(Path.Combine(root,"documents","direct.txt")));
            Check.True(!fixture.Output.Lines.Any(l=>l.Contains("[Approval")),"A full-access user write was still queued.");
            Check.Equal(0,model.Calls);
        });
        await suite.Case("mixed-case model file actions retain the file-specific approval gate and concrete preview", async root =>
        {
            var model=new ScriptedModel(Decision(new ToolRequest("CrEaTe_FiLe",Args(new {path="documents/mixed.txt",content="reviewed"}))),Decision());
            await using var fixture=await Fixture.CreateAsync(root,model,c=>{c.Permissions.ConfirmActions=false;c.Permissions.ConfirmFileChanges=true;c.Permissions.FullFileAccess=false;});
            await fixture.Engine.RespondAsync("Keep a note about our plan.","user",false,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","mixed.txt")),"Mixed-case tool naming bypassed file confirmation.");
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("[Approval 1]")&&l.Contains("+reviewed")),"The mixed-case file action did not create a concrete preview.");
            await fixture.Engine.ApproveAsync(1,CancellationToken.None);
            Check.Equal("reviewed",await File.ReadAllTextAsync(Path.Combine(root,"documents","mixed.txt")));
            Check.Equal(2,model.Calls);
        });
        await suite.Case("file writing permission denies user requests and remains authoritative over full access", async root =>
        {
            var model=new ScriptedModel();await using var fixture=await Fixture.CreateAsync(root,model,c=>{c.Permissions.WriteFiles=false;c.Permissions.FullFileAccess=true;});
            await fixture.Engine.RespondAsync("write documents/blocked.txt |blocked","user",false,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","blocked.txt")),"Full file access bypassed the disabled write permission.");
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("File writing is disabled.")),"The permission denial was not reported.");
            Check.Equal(0,model.Calls);
        });
        await suite.Case("scope rules apply to full-access autonomous writes and normal user proposals", async root =>
        {
            var model=new ScriptedModel(Decision(new ToolRequest("create_file",Args(new {path="outside/blocked.md",content="never"}))));
            await using var fixture=await Fixture.CreateAsync(root,model,c=>c.Permissions.FullFileAccess=true);
            await fixture.Engine.RespondAsync("Consider keeping a note.","environment",true,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"outside","blocked.md")),"Full access escaped its configured writable scope.");
            Check.True(fixture.Output.Lines.Any(l=>l.Contains("outside permissions.writableDirectories")),"The autonomous scope failure was not reported.");
            fixture.Config.Permissions.FullFileAccess=false;
            await Check.ThrowsAsync<UnauthorizedAccessException>(()=>fixture.Engine.RespondAsync("write outside/user.md |never","user",false,false,CancellationToken.None));
            Check.True(!File.Exists(Path.Combine(root,"outside","user.md")),"A user proposal escaped its writable scope.");
            Check.True(!fixture.Output.Lines.Any(l=>l.Contains("[Approval")),"An out-of-scope proposal was queued for approval.");
        });
        await suite.Case("autonomous file access can be disabled independently of ordinary user writes", async root =>
        {
            var model=new ScriptedModel(Decision(new ToolRequest("create_file",Args(new {path="documents/blocked-idea.md",content="never"}))));
            await using var fixture=await Fixture.CreateAsync(root,model,c=>{c.Permissions.AllowAutonomousFileActions=false;c.Permissions.FullFileAccess=true;});
            await fixture.Engine.RespondAsync("An autonomous opportunity.","environment",true,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","blocked-idea.md")),"An autonomous action ignored its dedicated permission.");
            Check.True(!fixture.Output.Lines.Any(l=>l.Contains("[Approval")),"Disabled autonomous writes still generated proposals.");
            await fixture.Engine.RespondAsync("write documents/user.txt |requested","user",false,false,CancellationToken.None);
            Check.Equal("requested",await File.ReadAllTextAsync(Path.Combine(root,"documents","user.txt")));Check.Equal(1,model.Calls);
        });
        await suite.Case("blocked autonomous file actions cannot publish or remember a fabricated success claim", async root =>
        {
            for(var policy=0;policy<2;policy++)
            {
                var caseRoot=Path.Combine(root,"policy-"+policy);
                var decision=JsonSerializer.Serialize(new CompanionDecision {Speak=true,Reply="I created the note successfully.",Memory="Successfully created documents/blocked.md.",Emotion=new(),Actions=[new("CREATE_FILE",Args(new {path="documents/blocked.md",content="never"}))]},AppConfig.Json);
                var model=new ScriptedModel(decision);
                await using var fixture=await Fixture.CreateAsync(caseRoot,model,c=>{c.Permissions.FullFileAccess=true;if(policy==0)c.Permissions.WriteFiles=false;else c.Permissions.AllowAutonomousFileActions=false;});
                await fixture.Engine.RespondAsync("An autonomous opportunity.","environment",true,false,CancellationToken.None);
                Check.True(!File.Exists(Path.Combine(caseRoot,"documents","blocked.md")),"A disabled policy allowed a write.");
                Check.Equal(0,fixture.Output.Lines.Count,"A blocked action published a success claim");
                Check.True(fixture.Memory.Recent.All(e=>e.Role is not ("assistant" or "note")),"A blocked action persisted a fabricated success fact.");
                Check.Equal(1,model.Calls);
            }
        });
    }

    private sealed class ScriptedModel(params string[] responses) : IChatModel
    {
        public int Calls {get;private set;}
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,bool json,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Calls++;
            if(Calls>responses.Length)throw new InvalidOperationException("Unexpected model call in deterministic file test.");
            return Task.FromResult(responses[Calls-1]);
        }
    }
    private sealed class CapturingOutput:ICompanionOutput
    {
        public List<string> Lines {get;}=[];
        public void Write(string text)=>Lines.Add(text);
    }
    private sealed class NoBrowser:IBrowserReader
    {
        public Task<BrowserPage> ReadAsync(CancellationToken ct)=>throw new InvalidOperationException("A file test attempted browser access.");
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public required AppConfig Config {get;init;}
        public required CompanionEngine Engine {get;init;}
        public required MemoryStore Memory {get;init;}
        public required AudioService Audio {get;init;}
        public required WeatherService Weather {get;init;}
        public required CapturingOutput Output {get;init;}
        public static async Task<Fixture> CreateAsync(string root,IChatModel model,Action<AppConfig>? configure=null)
        {
            var paths=new PortablePaths(root);var config=new AppConfig();
            config.Audio.Enabled=false;config.Browser.Enabled=false;
            config.Permissions.Microphone=false;config.Permissions.ScreenCapture=false;config.Permissions.ForegroundWindow=false;
            config.Permissions.SystemStatus=false;config.Permissions.WeatherNetwork=false;config.Permissions.ReadBrowser=false;
            configure?.Invoke(config);
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile),"You are a fictional companion. Answer in English and use actual tool results.");
            var memory=new MemoryStore(root,config.Memory);await memory.InitializeAsync();
            var emotions=new EmotionEngine(root);await emotions.InitializeAsync();
            var audio=new AudioService(root,config.Audio);var weather=new WeatherService(config);var output=new CapturingOutput();
            return new() {Config=config,Audio=audio,Weather=weather,Output=output,Memory=memory,
                Engine=new(paths,config,model,memory,emotions,new WindowsDesktop(config.Desktop),audio,output,weather,new NoBrowser()) {Paused=true}};
        }
        public async ValueTask DisposeAsync(){await Engine.DisposeAsync();await Audio.DisposeAsync();Weather.Dispose();}
    }
}
