using System.Text;
using System.Threading.Channels;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Browser;

namespace ComputerNeighbour;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        var ui=new ConsoleUi();
        using var shutdown=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;shutdown.Cancel();};
        try
        {
            if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 10/11 x64 is required.");
            var paths=PortablePaths.Discover(args);paths.InitializeEnvironment();
            using var instance=new FileStream(paths.Inside("data/instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var config=AppConfig.Load(paths);
            var desktop=new WindowsDesktop(config.Desktop);
            await using var audio=new AudioService(paths.Root,config.Audio);
            var initiative=Array.IndexOf(args,"--initiative");
            if(initiative>=0)
            {
                var mode=initiative+1<args.Length?args[initiative+1]:"";
                if(mode is not ("more" or "less"))throw new ArgumentException("--initiative requires more or less.");
                var profile=new UserProfileStore(paths.Root,config.UserName);
                await profile.InitializeAsync(shutdown.Token);
                await profile.ObserveAsync(mode=="more"?"Be more proactive.":"Be less proactive.","user",shutdown.Token);
                ui.Write(mode=="more"?"Standard initiative pacing restored.":"Reduced initiative pacing enabled.");
                return 0;
            }
            if(args.Contains("--init"))
            {
                var profile=new UserProfileStore(paths.Root,config.UserName);await profile.InitializeAsync(shutdown.Token);
                if(File.Exists(paths.Inside("data/memory/state.json")))
                {
                    var existing=new MemoryStore(paths.Root,config.Memory);await existing.InitializeAsync(shutdown.Token);
                    foreach(var entry in existing.Recent.Where(e=>e.Role=="user"))await profile.ObserveAsync(entry.Text,entry.SpeakerId,shutdown.Token,entry.At);
                }
                await new RelationshipStore(paths.Root,profile.PreferredName.Length>0?profile.PreferredName:config.UserName).InitializeAsync(shutdown.Token);
                ui.Write("Configuration ready: "+paths.Inside("config/appsettings.json")+"\nGrounded user profile: "+paths.Inside("data/memory/user-profile.json"));return 0;
            }
            if(args.Contains("--devices")) { ui.Write(audio.DescribeDevices());return 0; }
            if(args.Contains("--browser-diagnose"))
            {
                if(!config.Permissions.ReadBrowser||!config.Browser.Enabled)throw new UnauthorizedAccessException("Browser reading is disabled.");
                var page=await new WindowsBrowserReader(config.Browser,config.Desktop,config.Permissions.ReadWeb).ReadAsync(shutdown.Token);
                ui.Write($"Browser: {page.Browser}\nTitle: {page.Title}\nURL: {page.Url??"unavailable"}\nRead method: {page.ReadMethod}\nReadable characters: {page.Text.Length}\nTruncated: {page.Truncated}");return 0;
            }
            var documentDiagnosis=Array.IndexOf(args,"--document-diagnose");
            if(documentDiagnosis>=0)
            {
                if(!config.Permissions.ReadFiles||!config.Permissions.ReadOpenDocuments)throw new UnauthorizedAccessException("Open Notepad document reading is disabled.");
                if(documentDiagnosis+1>=args.Length||Path.GetFileName(args[documentDiagnosis+1])!=args[documentDiagnosis+1])throw new ArgumentException("--document-diagnose requires a Notepad filename.");
                var document=await new WindowsDocumentReader(config.Desktop).FindAsync(args[documentDiagnosis+1],true,shutdown.Token);
                if(document?.Text is not {} text)throw new IOException(document?.UnavailableReason??"No matching open Notepad tab was found.");
                ui.Write($"Notepad: matched requested filename.\nReadable characters: {Math.Min(text.Length,12000)}\nTruncated: {text.Length>12000}");return 0;
            }
            if(args.Contains("--diagnose")) { Diagnose(paths,config,audio,ui);return 0; }
            var memory=new MemoryStore(paths.Root,config.Memory);await memory.InitializeAsync(shutdown.Token);
            var emotions=new EmotionEngine(paths.Root);await emotions.InitializeAsync(shutdown.Token);
            await using var model=new LocalModel(paths,config.Model);
            var version=typeof(Program).Assembly.GetName().Version?.ToString(3)??"0.2.0";
            ui.Write($"ComputerNeighbour {version} · {config.CharacterName}\nModel: {Path.GetFileName(config.Model.ModelFile)}\nStarting local model; this may take a moment. /help for commands.");
            await model.StartAsync(shutdown.Token);
            using var weather=new WeatherService(config);
            await using var engine=new CompanionEngine(paths,config,model,memory,emotions,desktop,audio,ui,weather);
            await engine.GetRelationshipAsync(shutdown.Token);
            var keyName=config.Audio.PushToTalkVirtualKey is >=0x70 and <=0x87?$"F{config.Audio.PushToTalkVirtualKey-0x6F}":$"VK 0x{config.Audio.PushToTalkVirtualKey:X2}";
            ui.Write($"Ready. Type English or Russian. Hold {keyName} to talk. Replies are English.\nMicrophone={config.Permissions.Microphone}; screen={config.Permissions.ScreenCapture}; computer input={config.Permissions.DesktopInput}.");
            if(memory.RecoveryNotice is {} notice)ui.Write(notice);
            var queue=Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(16) { SingleReader=true,FullMode=BoundedChannelFullMode.Wait });
            var input=ReadInputAsync(queue.Writer,ui,engine,shutdown.Token);
            var microphone=MicrophoneAsync(queue.Writer,config,desktop,audio,ui,engine,shutdown.Token);
            var timer=TimerAsync(queue.Writer,config,engine,shutdown.Token);
            var lastWindow="";var wasAway=false;var nextOpportunity=DateTimeOffset.Now.AddSeconds(config.Proactive.MinIntervalSeconds);var lastOpportunity=DateTimeOffset.MinValue;
            try
            {
                await foreach(var evt in queue.Reader.ReadAllAsync(shutdown.Token))
                {
                    try
                    {
                        if(evt.Kind=="exit")break;
                        if(evt.Kind=="text" && evt.Text.StartsWith('/'))
                        {
                            var split=evt.Text.Split(' ',2,StringSplitOptions.RemoveEmptyEntries);var command=split[0].ToLowerInvariant();var arg=split.Length>1?split[1]:"";
                            switch(command)
                            {
                                case "/exit":case "/quit": shutdown.Cancel();break;
                                case "/help": ui.Write(Help);break;
                                case "/status": ui.Write($"Root: {paths.Root}\n{emotions.Status}\nMemory: {memory.StoredCharacters} recent characters; summary {memory.Summary.Length}; paused={engine.Paused}; mute={engine.Muted}.");break;
                                case "/pause": engine.Paused=true;engine.InterruptSpeech();ui.Write("Observation and proactive comments paused. Text chat and held-key microphone remain available.");break;
                                case "/resume": engine.Paused=false;ui.Write("Observation and proactive comments resumed.");break;
                                case "/mute": engine.Muted=!engine.Muted;engine.InterruptSpeech();ui.Write("Voice muted: "+engine.Muted);break;
                                case "/devices": ui.Write(audio.DescribeDevices());break;
                                case "/enroll": audio.EnrollSpeaker(arg);ui.Write("Hold your push-to-talk key and speak alone for several seconds to enroll: "+arg);break;
                                case "/approve": if(int.TryParse(arg,out var approve))await engine.ApproveAsync(approve,shutdown.Token);else ui.Write("Usage: /approve NUMBER");break;
                                case "/reject": if(int.TryParse(arg,out var reject))engine.Reject(reject);else ui.Write("Usage: /reject NUMBER");break;
                                case "/memory": ui.Write("Durable memory:\n"+memory.Summary);break;
                                case "/relationship": ui.Write(await engine.GetRelationshipAsync(shutdown.Token));break;
                                case "/character": ui.Write(config.CharacterName+"'s developing character:\n"+emotions.DescribeGrowth());break;
                                case "/browser": await engine.RespondAsync("Read the current browser page and summarize it.","user",false,false,shutdown.Token);break;
                                case "/read": await engine.RespondAsync("Read "+arg,"user",false,false,shutdown.Token);break;
                                case "/search": await engine.RespondAsync("Search for "+arg,"user",false,false,shutdown.Token);break;
                                case "/news": await engine.RespondAsync(arg.Length==0?"Find the latest technology news.":"Find recent news about "+arg,"user",false,false,shutdown.Token);break;
                                case "/sources": await engine.RespondAsync("Show the sources.","user",false,false,shutdown.Token);break;
                                case "/write":case "/create": await engine.RespondAsync("write "+arg,"user",false,false,shutdown.Token);break;
                                case "/edit": await engine.RespondAsync("edit "+arg,"user",false,false,shutdown.Token);break;
                                case "/compact": await engine.CompactAsync(shutdown.Token);ui.Write("Memory compaction finished.");break;
                                case "/forget": if(arg=="CONFIRM") {await engine.ForgetConversationAsync(shutdown.Token);ui.Write("Conversation memory and saved web sources erased. Character, profile, mood and relationship retained.");}else ui.Write("Type /forget CONFIRM to erase conversation memory.");break;
                                case "/look": if(!config.Permissions.ScreenCapture)ui.Write("Screen capture is disabled in config/appsettings.json.");else if(engine.Paused)ui.Write("Observation is paused; use /resume first.");else await engine.RespondAsync("Describe what you can actually see on my screen.","user",false,true,shutdown.Token);break;
                                default: ui.Write("Unknown command. /help");break;
                            }
                        }
                        else if(evt.Kind is "text" or "voice") await engine.RespondAsync(evt.Text,evt.Speaker,false,false,shutdown.Token);
                        else if(evt.Kind=="tick" && !engine.Paused && config.Proactive.Enabled && engine.CanInitiate)
                        {
                            var now=DateTimeOffset.Now;var idle=desktop.GetIdleTime();
                            var window=config.Permissions.ForegroundWindow?desktop.GetForegroundContext():"";
                            var away=idle.TotalSeconds>=config.Proactive.AwayAfterSeconds;
                            var trigger=config.Proactive.TriggerOnReturnFromIdle&&wasAway&&!away?"User returned from inactivity.":config.Proactive.TriggerOnWindowChange&&lastWindow!=""&&window!=lastWindow?"The active window changed: "+window:"Scheduled opportunity.";
                            wasAway=away;lastWindow=window;
                            var quiet=IsQuietHour(now.Hour,config.Proactive.QuietStartHour,config.Proactive.QuietEndHour);
                            var triggered=trigger!="Scheduled opportunity.";
                            var intervalScale=engine.InitiativeIntervalScale;
                            if(!quiet&&!away&&engine.UnansweredMessages<config.Proactive.MaxUnansweredMessages && now-lastOpportunity>TimeSpan.FromSeconds(config.Proactive.CooldownSeconds*intervalScale) && now-engine.LastInteraction>TimeSpan.FromSeconds(config.Proactive.MinIntervalSeconds*intervalScale) && (now>=nextOpportunity||triggered))
                            {
                                lastOpportunity=now;nextOpportunity=now.AddSeconds(Random.Shared.Next(config.Proactive.MinIntervalSeconds,config.Proactive.MaxIntervalSeconds+1)*intervalScale);
                                await engine.RespondAsync(trigger,"environment",true,false,shutdown.Token);
                            }
                            if(memory.NeedsCompaction)await engine.CompactAsync(shutdown.Token);
                        }
                    }
                    catch(OperationCanceledException) when(shutdown.IsCancellationRequested) {break;}
                    catch(Exception ex) {ui.Write("[Error] "+ex.Message);}
                }
            }
            finally { shutdown.Cancel();await Task.WhenAll(input,microphone,timer); }
            ui.Write("Goodbye. Memory saved locally.");return 0;
        }
        catch(OperationCanceledException) {ui.Write("Stopped.");return 0;}
        catch(Exception ex) {ui.Write("[Startup] "+ex.Message);return 1;}
    }
    public static bool IsQuietHour(int hour,int start,int end)=>start==end?false:start<end?hour>=start&&hour<end:hour>=start||hour<end;
    private static async Task ReadInputAsync(ChannelWriter<AppEvent> writer,ConsoleUi ui,CompanionEngine engine,CancellationToken ct)
    {
        try {while(!ct.IsCancellationRequested) {var line=await ui.ReadLineAsync(ct);if(line is null) {await writer.WriteAsync(new("exit","","user"),ct);return;} if(!string.IsNullOrWhiteSpace(line)) {engine.InterruptSpeech();await writer.WriteAsync(new("text",line,"user"),ct);}}}catch(OperationCanceledException) { }
    }
    private static async Task MicrophoneAsync(ChannelWriter<AppEvent> writer,AppConfig config,WindowsDesktop desktop,AudioService audio,ConsoleUi ui,CompanionEngine engine,CancellationToken ct)
    {
        if(!config.Permissions.Microphone || !config.Audio.Enabled)return;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                if(!desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey)) {await Task.Delay(25,ct);continue;}
                engine.ObserveBrowser();engine.BeginListening();ui.Write("[Microphone] Listening while key is held…");
                try
                {
                    var captured=await audio.ListenWhileHeldAsync(()=>desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey),ct);
                    if(captured is not null) {var speaker=captured.IsKnownSpeaker?captured.SpeakerId:"unidentified-"+DateTimeOffset.Now.ToString("HHmmssfff");ui.Write($"[{speaker}; confidence {captured.SpeakerConfidence:F2}] {captured.Text}");await writer.WriteAsync(new("voice",captured.Text,speaker),ct);}
                }
                catch(Exception ex) when(ex is not OperationCanceledException) {ui.Write("[Microphone] "+ex.Message);}
                finally {engine.EndListening();}
                while(desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey))await Task.Delay(25,ct);
            }
        }
        catch(OperationCanceledException) { }
    }
    private static async Task TimerAsync(ChannelWriter<AppEvent> writer,AppConfig config,CompanionEngine engine,CancellationToken ct)
    {
        try {using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));var next=DateTimeOffset.Now;while(await timer.WaitForNextTickAsync(ct)){engine.ObserveBrowser();if(DateTimeOffset.Now>=next){writer.TryWrite(new("tick","","environment"));next=DateTimeOffset.Now.AddSeconds(config.Proactive.CheckSeconds);}}}catch(OperationCanceledException) { }
    }
    private static void Diagnose(PortablePaths paths,AppConfig config,AudioService audio,ConsoleUi ui)
    {
        ui.Write("Portable root: "+paths.Root);
        foreach(var path in new[]{config.Model.Executable,config.Model.ModelFile,config.Model.ProjectorFile,config.Audio.WhisperExecutable,config.Audio.WhisperModel,config.Audio.PiperExecutable,config.Audio.PiperModel,config.Audio.PiperModel+".json",config.CharacterFile})ui.Write($"{(File.Exists(paths.Inside(path))?"OK":"MISSING")} {path}");
        ui.Write(audio.DescribeDevices());
    }
    private sealed record AppEvent(string Kind,string Text,string Speaker);
    private const string Help="""
    Type a message in English or Russian. Evelyn answers in English.
    Hold F10 (configurable) to record; release to transcribe. Ctrl+C or /exit to quit.
    /look          Analyse current screen
    /browser       Read and summarise the current/last active browser tab
    /read TARGET   Read a public URL or a text filename/path
    /search QUERY  Search the live internet, read results and show source URLs
    /news [TOPIC]  Find recent news (technology by default)
    /sources       Show verified links from the last lookup
    /write PATH | TEXT  Prepare a new text file
    /edit PATH | OLD | NEW  Prepare an exact replacement in a text file
    /pause /resume Pause/resume observation and unsolicited comments
    /mute          Toggle spoken replies
    /enroll NAME   Enroll the next held-key recording as a speaker (speak alone)
    /devices       List input/output audio devices
    /status        Mood, relationship and memory status
    /memory        Show compressed factual memory
    /relationship  Show the separate persistent attitude toward the main user
    /character     Show developing and established habits and interests
    /compact       Compact memory now with the model
    /forget CONFIRM Erase conversation memory
    /approve N     Approve a displayed computer action (expires in 2 minutes)
    /reject N      Reject an action
    Edit config/appsettings.json or characters/neighbour.md; restart to reload config.
    """;
}
