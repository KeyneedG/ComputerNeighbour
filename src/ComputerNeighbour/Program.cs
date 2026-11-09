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
            await using var model=new LocalModel(paths,config.Model,shutdown.Token);
            await using var learning=new DialogueLearning(paths,config,model,ui);
            var version=typeof(Program).Assembly.GetName().Version?.ToString(3)??"0.2.0";
            ui.Write($"ComputerNeighbour {version} · {config.CharacterName}\nModel: {Path.GetFileName(config.Model.EffectiveModelFile)}\nStarting local model; this may take a moment. /help for commands.");
            await learning.StartModelAsync(shutdown.Token);
            using var weather=new WeatherService(config);
            await using var regularEngine=new CompanionEngine(paths,config,model,memory,emotions,desktop,audio,ui,weather);
            await regularEngine.GetRelationshipAsync(shutdown.Token);
            await regularEngine.InitializePersonalLifeAsync(shutdown.Token);
            await regularEngine.RestoreCurrentWorkAsync(shutdown.Token);
            await regularEngine.RestoreMissedGoogleRequestAsync(shutdown.Token);
            await using var sessions=new CompanionSession(regularEngine,ui);
            var keyName=config.Audio.PushToTalkVirtualKey is >=0x70 and <=0x87?$"F{config.Audio.PushToTalkVirtualKey-0x6F}":$"VK 0x{config.Audio.PushToTalkVirtualKey:X2}";
            ui.Write($"Ready. Type English or Russian. Hold {keyName} to talk. Replies are English.\nMicrophone={config.Permissions.Microphone}; screen={config.Permissions.ScreenCapture}; computer input={config.Permissions.DesktopInput}.");
            if(memory.RecoveryNotice is {} notice)ui.Write(notice);
            var queue=Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(16) { SingleReader=true,FullMode=BoundedChannelFullMode.Wait });
            var queued = new QueueState();
            // A redirected Windows stdin can block inside ReadLineAsync before returning a task.
            var input=Task.Run(()=>ReadInputAsync(queue.Writer,queued,ui,sessions,shutdown.Token));
            var microphone=MicrophoneAsync(queue.Writer,queued,config,desktop,audio,ui,sessions,shutdown.Token);
            var timer=TimerAsync(queue.Writer,queued,config,sessions,shutdown.Token);
            ValueTask SignalTrainingDone(CancellationToken ct) => queue.Writer.WriteAsync(new("lora-done","","system-event"),ct);
            var trainingDone = false;
            var lastWindow="";var wasAway=false;var nextOpportunity=DateTimeOffset.Now.AddSeconds(config.Proactive.MinIntervalSeconds);var lastOpportunity=DateTimeOffset.MinValue;
            try
            {
                await foreach(var evt in queue.Reader.ReadAllAsync(shutdown.Token))
                {
                    if (evt.Kind == "tick") queued.ConsumeTick();
                    if (evt.Kind is "text" or "voice" or "exit") queued.ConsumeInput();
                    try
                    {
                        if(evt.Kind=="exit")break;
                        if (evt.Kind == "lora-done") trainingDone = true;
                        if (trainingDone && evt.Kind is ("tick" or "lora-done") && !queued.HasInput)
                        {
                            trainingDone = false;
                            var completed = await learning.FinishTrainingAsync(shutdown.Token);
                            if (!learning.IsTraining) await regularEngine.NoteTrainingResultAsync(completed, shutdown.Token);
                        }
                        if(evt.Kind=="lora-done")continue;
                        if(evt.Kind=="text"&&CompanionSession.IsIncognitoCommand(evt.Text))
                        {
                            if(evt.Text.Trim().Equals("/stop incognito",StringComparison.OrdinalIgnoreCase))
                                await learning.EndPreviewAsync(shutdown.Token);
                            else if(learning.IsTraining)
                            {
                                ui.Write("[LoRA] Wait for training to finish or use /lora cancel before entering incognito.");
                                continue;
                            }
                            await sessions.TryHandleCommandAsync(evt.Text,shutdown.Token);
                            learning.DiscardChoice();
                            ui.SetIncognito(sessions.Current.Engine.IsIncognito);
                            continue;
                        }
                        var active=sessions.Current;
                        if(evt.Session is not null&&!ReferenceEquals(evt.Session,active))continue;
                        var engine=active.Engine;
                        if(evt.Kind=="text" && evt.Text.StartsWith('/'))
                        {
                            if(await engine.TryHandleAccessCommandAsync(evt.Text,shutdown.Token))continue;
                            var split=evt.Text.Split(' ',2,StringSplitOptions.RemoveEmptyEntries);var command=split[0].ToLowerInvariant();var arg=split.Length>1?split[1]:"";
                            switch(command)
                            {
                                case "/exit":case "/quit": shutdown.Cancel();break;
                                case "/help": ui.Write(Help);break;
                                case "/lora": await learning.HandleAsync(arg,engine,SignalTrainingDone,shutdown.Token);break;
                                case "/status": ui.Write(engine.DescribeStatus());break;
                                case "/life": ui.Write(engine.DescribePersonalLife(DateTimeOffset.Now));break;
                                case "/web":
                                    if(arg.Equals("google",StringComparison.OrdinalIgnoreCase))
                                        await engine.RunBackgroundAsync(async ct =>
                                        {
                                            ui.Write(await engine.TestPersonalGoogleAsync(ct));
                                            return true;
                                        }, () => queued.HasInput, shutdown.Token);
                                    else ui.Write(await engine.DescribePersonalWebAsync(DateTimeOffset.Now, shutdown.Token));
                                    break;
                                case "/diary": ui.Write(await engine.ReadDiaryAsync(shutdown.Token));break;
                                case "/pause":
                                    await engine.SetBackgroundPauseAsync(true, shutdown.Token);
                                    if (!engine.IsIncognito) learning.CancelAutomaticTraining();
                                    ui.Write("I put my background activities on pause. /resume picks them back up.");
                                    break;
                                case "/resume":
                                    await engine.SetBackgroundPauseAsync(false, shutdown.Token);
                                    ui.Write("I can pick up my background activities again.");
                                    break;
                                case "/mute": engine.Muted=!engine.Muted;engine.InterruptSpeech();ui.Write("Voice muted: "+engine.Muted);break;
                                case "/devices": ui.Write(audio.DescribeDevices());break;
                                case "/enroll":
                                    if(engine.IsIncognito)ui.Write("Voice enrollment is available in the regular session.");
                                    else {audio.EnrollSpeaker(arg);ui.Write("Hold your push-to-talk key and speak alone for several seconds to enroll: "+arg);}
                                    break;
                                case "/approve": if(int.TryParse(arg,out var approve))await engine.ApproveAsync(approve,shutdown.Token);else ui.Write("Usage: /approve NUMBER");break;
                                case "/reject": if(int.TryParse(arg,out var reject))await engine.RejectFromConsoleAsync(reject,shutdown.Token);else ui.Write("Usage: /reject NUMBER");break;
                                case "/memory": ui.Write((engine.IsIncognito?"Incognito memory:\n":"Durable memory:\n")+engine.MemorySummary);break;
                                case "/thoughts": ui.Write(await engine.GetThoughtsAsync(shutdown.Token));break;
                                case "/relationship": ui.Write(await engine.GetRelationshipAsync(shutdown.Token));break;
                                case "/character": ui.Write(config.CharacterName+"'s developing character:\n"+engine.DescribeCharacter());break;
                                case "/browser": await engine.RespondAsync("Read the current browser page and summarize it.","user",false,false,shutdown.Token);break;
                                case "/read": await engine.RespondAsync("Read "+arg,"user",false,false,shutdown.Token);break;
                                case "/search": await engine.RespondAsync("Search for "+arg,"user",false,false,shutdown.Token);break;
                                case "/news": await engine.RespondAsync(arg.Length==0?"Find the latest technology news.":"Find recent news about "+arg,"user",false,false,shutdown.Token);break;
                                case "/sources": await engine.RespondAsync("Show the sources.","user",false,false,shutdown.Token);break;
                                case "/write":case "/create": await engine.RespondAsync("write "+arg,"user",false,false,shutdown.Token);break;
                                case "/edit": await engine.RespondAsync("edit "+arg,"user",false,false,shutdown.Token);break;
                                case "/compact": await engine.CompactAsync(shutdown.Token);ui.Write("Memory compaction finished.");break;
                                case "/forget":
                                    if(arg=="CONFIRM")
                                    {
                                        await engine.ForgetConversationAsync(shutdown.Token);
                                        if(!engine.IsIncognito)await learning.ClearAsync(shutdown.Token);
                                        ui.Write("Conversation memory, saved tasks and their reminders, web sources and turn notes erased." +
                                            (engine.IsIncognito?"":" Personal diary, goal checkpoint and LoRA data cleared."));
                                    }
                                    else ui.Write("Type /forget CONFIRM to erase conversation memory, saved tasks and LoRA training data for the current session.");
                                    break;
                                case "/task":case "/tasks": await engine.RespondAsync(evt.Text,"user",false,false,shutdown.Token);break;
                                case "/work":case "/do":case "/google": await engine.RespondAsync(evt.Text,"user",false,false,shutdown.Token);break;
                                case "/look": await engine.RespondAsync("Describe what you can actually see on my screen.","user",false,true,shutdown.Token);break;
                                default: ui.Write("Unknown command. /help");break;
                            }
                        }
                        else if(evt.Kind is "text" or "voice")
                        {
                            if (evt.Kind == "text" && !engine.IsIncognito && await learning.TryChooseAsync(evt.Text, shutdown.Token))
                                continue;
                            learning.DiscardChoice();
                            if(evt.Kind=="voice")ui.Write($"[{evt.Speaker}; confidence {evt.SpeakerConfidence:F2}] {evt.Text}");
                            await engine.RespondAsync(evt.Text,evt.Speaker,false,false,shutdown.Token);
                            if (!engine.IsIncognito && engine.BackgroundDeferred(DateTimeOffset.Now)) learning.CancelAutomaticTraining();
                            if (evt.Kind == "text" && !engine.HasPriorityWork)
                                await engine.RunBackgroundAsync(async ct =>
                                {
                                    await learning.MaybeCompareAsync(engine, requested: false, ct);
                                    return false;
                                }, () => queued.HasInput, shutdown.Token);
                        }
                        else if(evt.Kind=="tick"&&!engine.IsIncognito)
                        {
                            await engine.RunBackgroundAsync(async activity =>
                            {
                                var now=DateTimeOffset.Now;
                                if(await engine.DeliverWorkTaskReminderAsync(now,activity))return true;
                                if(await engine.TickRequestedGoogleAsync(now,desktop.GetInteractiveIdleTime(),activity))return true;
                                if(engine.HasPriorityWork)return false;
                                var maySpeak=!engine.BackgroundDeferred(now)&&config.Proactive.Enabled&&engine.CanInitiate;
                                var idle=maySpeak||engine.AutonomyEnabled?desktop.GetInteractiveIdleTime():null;
                                var away=idle >= TimeSpan.FromSeconds(config.Proactive.AwayAfterSeconds);
                                var returned=maySpeak&&idle.HasValue&&config.Proactive.TriggerOnReturnFromIdle&&wasAway&&!away;
                                if (idle.HasValue) wasAway=away;
                                if (returned) engine.NoteReturnToComputer();
                                var quiet=IsQuietHour(now.Hour,config.Proactive.QuietStartHour,config.Proactive.QuietEndHour);
                                var intervalScale=engine.InitiativeIntervalScale;
                                var cooldownOver=now-lastOpportunity>TimeSpan.FromSeconds(config.Proactive.CooldownSeconds*intervalScale);
                                var handled=await engine.TickNeedsAsync(now,idle,quiet,cooldownOver&&!engine.HasPersonalNotice(now,idle,quiet),
                                    ()=>desktop.CanUseSketchWorkspace(checkLoad:true,maxSampleSeconds:Math.Max(15,config.Proactive.CheckSeconds*2.0)),
                                    ()=>desktop.GetInteractiveIdleTime()>=TimeSpan.FromSeconds(config.Needs.SketchIdleSeconds)&&desktop.CanUseSketchWorkspace(checkLoad:false),
                                    activity, scheduledPersonalLife:true);
                                if (!handled)
                                    handled=await engine.TickPersonalLifeAsync(now,idle,quiet,cooldownOver,learning,
                                        SignalTrainingDone,activity,shutdown.Token);
                                if(handled) {lastOpportunity=DateTimeOffset.Now;return true;}
                                if(!maySpeak||engine.IsResting||!idle.HasValue)return false;
                                var window=config.Permissions.ForegroundWindow?desktop.GetForegroundContext():"";
                                var trigger=returned?"User returned from inactivity.":config.Proactive.TriggerOnWindowChange&&lastWindow!=""&&window!=lastWindow?"The active window changed.":"Scheduled opportunity.";
                                lastWindow=window;
                                var remarkLimit=config.Proactive.MaxUnansweredMessages;
                                if(config.Needs.Enabled&&config.Needs.InviteWhenLonely&&remarkLimit>1)remarkLimit--;
                                if(!engine.WaitingForCompanyReply&&!quiet&&!away&&engine.UnansweredMessages<remarkLimit && cooldownOver && now-engine.LastInteraction>TimeSpan.FromSeconds(config.Proactive.MinIntervalSeconds*intervalScale) && (now>=nextOpportunity||trigger!="Scheduled opportunity."))
                                {
                                    lastOpportunity=now;nextOpportunity=now.AddSeconds(Random.Shared.Next(config.Proactive.MinIntervalSeconds,config.Proactive.MaxIntervalSeconds+1)*intervalScale);
                                    await engine.RespondAsync(trigger,"environment",true,false,activity);
                                }
                                if(memory.NeedsCompaction)await engine.CompactAsync(activity);
                                return false;
                            }, () => queued.HasInput, shutdown.Token);
                        }
                    }
                    catch(OperationCanceledException) when(shutdown.IsCancellationRequested) {break;}
                    catch(Exception ex) {ui.Write("[Error] "+ex.Message);}
                }
            }
            finally
            {
                shutdown.Cancel();
                await Task.WhenAll(microphone,timer);
                if (!Console.IsInputRedirected || input.IsCompleted) await input;
            }
            ui.Write(sessions.Current.Engine.IsIncognito?"Goodbye. Incognito changes discarded.":"Goodbye. Memory saved locally.");return 0;
        }
        catch(OperationCanceledException) {ui.Write("Stopped.");return 0;}
        catch(Exception ex) {ui.Write("[Startup] "+ex.Message);return 1;}
    }
    public static bool IsQuietHour(int hour,int start,int end)=>start==end?false:start<end?hour>=start&&hour<end:hour>=start||hour<end;
    private static async Task ReadInputAsync(ChannelWriter<AppEvent> writer,QueueState queued,ConsoleUi ui,CompanionSession sessions,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var line=await ui.ReadLineAsync(ct);
                if (ct.IsCancellationRequested) return;
                var engine=sessions.Current.Engine;
                if(line is null)
                {
                    queued.AddInput();
                    engine.InterruptActivity();
                    await writer.WriteAsync(new("exit","","user"),ct);
                    return;
                }
                if(string.IsNullOrWhiteSpace(line))continue;
                queued.AddInput();
                if(line.Trim().ToLowerInvariant() is not ("/access" or "/status" or "/thoughts" or "/life" or "/diary" or "/web" or "/task current" or "/help"))
                {
                    engine.InterruptActivity();
                    engine.InterruptSpeech();
                }
                else engine.InterruptBackground();
                await writer.WriteAsync(new("text",line,"user"),ct);
            }
        }
        catch(OperationCanceledException) { }
    }
    private static async Task MicrophoneAsync(ChannelWriter<AppEvent> writer,QueueState queued,AppConfig config,WindowsDesktop desktop,AudioService audio,ConsoleUi ui,CompanionSession sessions,CancellationToken ct)
    {
        if(!config.Permissions.Microphone || !config.Audio.Enabled)return;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var started=sessions.Current;
                var engine=started.Engine;
                if(engine.IsIncognito||started.InputToken.IsCancellationRequested||!desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey))
                {await Task.Delay(25,ct);continue;}
                using var capture=CancellationTokenSource.CreateLinkedTokenSource(ct,started.InputToken);
                if(capture.IsCancellationRequested)continue;
                engine.ObserveBrowser();engine.BeginListening();ui.Write("[Microphone] Listening while key is held…");
                try
                {
                    var captured=await audio.ListenWhileHeldAsync(()=>desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey),capture.Token);
                    if(captured is not null&&!capture.IsCancellationRequested&&ReferenceEquals(started,sessions.Current))
                    {
                        var speaker=captured.IsKnownSpeaker?captured.SpeakerId:"unidentified-"+DateTimeOffset.Now.ToString("HHmmssfff");
                        queued.AddInput();
                        try { await writer.WriteAsync(new("voice",captured.Text,speaker,started,captured.SpeakerConfidence),capture.Token); }
                        catch { queued.ConsumeInput(); throw; }
                    }
                }
                catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
                catch(Exception ex) when(ex is not OperationCanceledException) {ui.Write("[Microphone] "+ex.Message);}
                finally {engine.EndListening();}
                while(desktop.IsKeyDown(config.Audio.PushToTalkVirtualKey))await Task.Delay(25,ct);
            }
        }
        catch(OperationCanceledException) { }
    }
    private static async Task TimerAsync(ChannelWriter<AppEvent> writer,QueueState queued,AppConfig config,CompanionSession sessions,CancellationToken ct)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var next=DateTimeOffset.Now;
            while(await timer.WaitForNextTickAsync(ct))
            {
                var active=sessions.Current;
                if(active.Engine.IsIncognito||active.InputToken.IsCancellationRequested)continue;
                active.Engine.ObserveBrowser();
                if(DateTimeOffset.Now<next)continue;
                if (queued.TryQueueTick() && !writer.TryWrite(new("tick","","environment",active))) queued.ConsumeTick();
                next=DateTimeOffset.Now.AddSeconds(config.Proactive.CheckSeconds);
            }
        }
        catch(OperationCanceledException) { }
    }
    private static void Diagnose(PortablePaths paths,AppConfig config,AudioService audio,ConsoleUi ui)
    {
        ui.Write("Portable root: "+paths.Root);
        foreach(var path in new[]{config.Model.Executable,config.Model.EffectiveModelFile,config.Model.EffectiveProjectorFile,config.Audio.WhisperExecutable,config.Audio.WhisperModel,config.Audio.PiperExecutable,config.Audio.PiperModel,config.Audio.PiperModel+".json",config.CharacterFile})ui.Write($"{(File.Exists(paths.Inside(path))?"OK":"MISSING")} {path}");
        ui.Write(config.Audio.UsesExpressiveVoice?$"Voice: Qwen3-TTS / {config.Audio.QwenTtsVoice} ({config.Audio.QwenTtsDevice})":"Voice: Piper");
        if(config.Audio.UsesExpressiveVoice)
            foreach(var path in new[]{config.Audio.QwenTtsExecutable,config.Audio.QwenTtsModel})
                ui.Write($"{(File.Exists(paths.Inside(path))?"OK":"MISSING")} {path}");
        ui.Write(audio.DescribeDevices());
    }
    private sealed record AppEvent(string Kind,string Text,string Speaker,CompanionSession.View? Session=null,double SpeakerConfidence=0);
    private sealed class QueueState
    {
        private int input;
        private int tick;
        public bool HasInput => Volatile.Read(ref input) > 0;
        public void AddInput() => Interlocked.Increment(ref input);
        public void ConsumeInput() => Interlocked.Decrement(ref input);
        public bool TryQueueTick() => Interlocked.CompareExchange(ref tick, 1, 0) == 0;
        public void ConsumeTick() => Volatile.Write(ref tick, 0);
    }
    private const string Help="""
    Type a message in English or Russian. Evelyn answers in English.
    Hold F10 (configurable) to record; release to transcribe. Ctrl+C or /exit to quit.
    /look          Analyse current screen
    /google QUESTION  Select a screen region and search it in Google AI Mode in Chrome
    /browser       Read and summarise the current/last active browser tab
    /read TARGET   Read a public URL or a text filename/path
    /search QUERY  Search the live internet, read results and show source URLs
    /news [TOPIC]  Find recent news (technology by default)
    /sources       Show verified links from the last lookup
    /write PATH | TEXT  Prepare a new text file
    /edit PATH | OLD | NEW  Prepare an exact replacement in a text file
    /pause /resume Pause/resume background observation and Evelyn's own goals
    /mute          Toggle spoken replies
    /enroll NAME   Enroll the next held-key recording as a speaker (speak alone)
    /devices       List input/output audio devices
    /status        Mood, personal drives, active goal and memory status
    /life          Show the current personal project, next step, and projects queued for later
    /web           Show confirmed background searches, articles, pictures and Google results
    /web google    Try one public question in the dedicated Google AI Mode browser
    /diary         Read recent personal diary entries
    /memory        Show compressed factual memory
    /thoughts      Show recent interpretations, checks and turn outcomes
    /relationship  Show the separate persistent attitude toward the main user
    /character     Show developing and established habits and interests
    /tasks         List saved work tasks and upcoming reminders
    /task          Show task commands for saving, resuming, and linking materials
    /start incognito  Start a temporary copy of the current session for experiments
    /stop incognito   Discard the temporary state and return to the regular session
    /lora help        Show local training commands for approved dialogue examples
    /compact       Compact memory now with the model
    /forget CONFIRM Erase conversation memory and saved tasks
    /approve N     Approve a displayed computer action (expires in 2 minutes)
    /work TASK     Carry out a task in steps and check the results
    /do [TASK]     Recheck and carry out a concrete request. Omit TASK to use your last request.
    /task current  Show the current task and what actually happened
    /task retry    Continue the current task from its checkpoint
    /task posted URL  Record a post you verified yourself
    /access        Show the computer access mode
    /full access   Permit automatic actions for explicit tasks in this session
    /ask before edit  Ask before changes and desktop input
    /approve for me   Delegate approval for the current or next explicit task
    /desktop on|off   Enable or disable guided desktop input for the session
    /access default   Restore configured access
    /cancel        Stop the workflow and discard its pending action
    /reject N      Reject an action
    Edit config/appsettings.json or characters/neighbour.md; restart to reload config.
    """;
}
