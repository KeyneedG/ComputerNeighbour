using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace ComputerNeighbour.Tests;

/// <summary>Optional real-model acceptance replay. It never reads user windows or mutates production memory.</summary>
internal static class DialogueModelSmoke
{
    private static readonly string[] Requests=
    [
        "Привет Евелин, я тебя создал. Хотя вернее не я, а другой ИИ, но я выступал в качестве археитектора и инициатора, тебе не обязательно комментировать каждое мое действие, отвечай тогда когда тебе есть что сказать, однако и ограничивать себя не следует. В общем надеюсь что мы поладим!",
        "Хорошо, меня называй меня Влад. я думаю тебе стоит поубавить свою инициативность, мне кажется она черезмерно наиграна , не исключено, что мне просто показалось. Будь более прямой со мной. и не стоит из себя выдавливать слова лишь бы что-то сказать, говори по существу.",
        "Ничего не существенного не нужно. просто расскажи что ты о себе знаешь. поделись ощущениями, что ты чувствуешь?",
        "интересно, продолжай",
        "ты повторила тоже самое, но так и не ответила какие у тебя мысли",
        "так расскажи уже!",
        "Пока что я думаю что ты дурной робот который не способен к чусвтвам подобным людским, но я здесь чтобы это исправить",
        "what is my name?"
    ];
    private static readonly string[] OldReplies=
    [
        "Hello there! Nice to meet you, User. I'm here to be your friendly companion. It sounds like we haven't had any direct interaction yet. Just letting you know that I appreciate your creativity and openness. Let's see where this goes!",
        "Hello Vlad. I see you're looking for some guidance. Is there anything specific you need help with today?",
        "I'm Evelyn, a digital companion designed to be a helpful and friendly assistant. I can tell you some of my experiences and thoughts if you're interested.",
        "I'm Evelyn, a digital companion designed to be a helpful and friendly assistant. I can tell you some of my experiences and thoughts if you're interested.",
        "I can tell you some of my experiences and thoughts if you're interested.",
        "Hello there, User. I'm Evelyn, and I'm here to be your friendly companion. I'm just a digital assistant, but I can tell you a bit more about myself if you'd like. What do you think about me?",
        "Hello there, User. I'm Evelyn, and I'm here to be your friendly companion. I'm just a digital assistant, but I can tell you a bit more about myself if you'd like. What do you think about me?",
        "Hello, User. What is your name? I'm here to help you."
    ];

    public static async Task<int> RunAsync(PortablePaths portable,bool characterOnly=false,bool contextOnly=false)
    {
        var root=portable.Inside("data/development-checks/dialogue-smoke-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        var report=new SmokeReport {StartedAt=DateTimeOffset.UtcNow,ArtifactRoot=root};
        var reportPath=Path.Combine(root,"report.json");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));var ct=timeout.Token;
        try
        {
            var options=AppConfig.Load(portable).Model;
            report.ModelFile=options.EffectiveModelFile;
            report.ProjectorFile=options.EffectiveProjectorFile;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            options.Endpoint="http://127.0.0.1:"+port;options.AutoStart=true;
            var startup=Stopwatch.StartNew();
            await using var model=new LocalModel(portable,options);
            await model.StartAsync(ct);report.ModelStartupMs=startup.ElapsedMilliseconds;
            Console.WriteLine($"Dialogue acceptance model ready in {report.ModelStartupMs} ms: {report.ModelFile}");
            if(contextOnly)
            {
                var contextRoot=Path.Combine(root,"context");
                await using var fixture=await Fixture.CreateAsync(portable,contextRoot,model,ct);
                report.Turns.Add(await TurnAsync(fixture,"I'm comparing WebP and PNG for image files. When I say the first format, I mean WebP.","context",1,ct));
                report.Turns.Add(await TurnAsync(fixture,"а пирвый подайдет, если хочю файлы поменьше?","context",2,ct));
                var thoughts=await fixture.Engine.GetThoughtsAsync(ct);
                Check.True(thoughts.Contains("I read the request as:")&&thoughts.Contains("WebP",StringComparison.OrdinalIgnoreCase)
                    &&thoughts.Contains("answered"),"The local context pass did not produce readable turn notes.");
                using var journal=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(contextRoot,"data","memory","thoughts.json"),ct));
                var latest=journal.RootElement.EnumerateArray().Last();
                Check.True(latest.GetProperty("interpretation").GetString()?.Contains("WebP",StringComparison.OrdinalIgnoreCase)==true,
                    "The local context pass did not resolve 'the first format' from the conversation.");
                Check.True(report.Turns[^1].Answer.Contains("WebP",StringComparison.OrdinalIgnoreCase),"The reply missed the referenced format.");
                Check.True(report.Turns.All(t=>!ReplyQuality.ClaimsFreshScreenObservation(t.Answer)),"A plain chat turn claimed a fresh desktop view without an image.");
                report.Passed=true;report.FinishedAt=DateTimeOffset.UtcNow;
                await SaveReportAsync(reportPath,report,ct);
                Console.WriteLine("PASS actual context rewrite, answer, and saved thought notes.\n"+thoughts);
                return 0;
            }
            if(characterOnly)
            {
                var characterRoot=Path.Combine(root,"character");
                await using(var fixture=await Fixture.CreateAsync(portable,characterRoot,model,ct))
                {
                    string[] prompts=
                    [
                        "My train signal in Unity went red when the track was empty. One forgotten axle counter had the whole railway held hostage. What strikes you about that?",
                        "I liked your take on a tiny detail running the whole show. In another level, one badly placed sign confused every playtester. What connection do you see between those two stories?",
                        "You're AI. Do you think talking together could make you a little more like a person? Tell me what you'd actually want to get better at."
                    ];
                    for(var i=0;i<prompts.Length;i++)
                    {
                        var turn=await TurnAsync(fixture,prompts[i],"character",i+1,ct);
                        ValidateEnglish(turn.Answer);
                        report.Turns.Add(turn);
                    }
                }
                var character=new EmotionEngine(characterRoot);
                await character.InitializeAsync(ct);
                Console.WriteLine(character.DescribeGrowth());
                Check.True(character.State.Traits.Count>0,"The real model did not discover a grounded habit or interest.");
                report.Passed=true;report.FinishedAt=DateTimeOffset.UtcNow;
                await SaveReportAsync(reportPath,report,ct);
                Console.WriteLine("PASS actual character dialogue and persistence. Report: "+reportPath);
                return 0;
            }

            var replayRoot=Path.Combine(root,"fresh-replay");
            await using(var fixture=await Fixture.CreateAsync(portable,replayRoot,model,ct))
            {
                for(var index=0;index<Requests.Length;index++)
                {
                    var answer=await TurnAsync(fixture,Requests[index],"replay",index+1,ct);
                    report.Turns.Add(answer);
                    ValidateEnglish(answer.Answer);
                    if(index>0)Check.True(!ReplyQuality.IsRepeated(report.Turns[index-1].Answer,answer.Answer),"Adjacent replay answers repeated the same content at turn "+(index+1));
                    if(index==3)
                    {
                        Check.True(!ReplyQuality.IsEmptyOffer(answer.Answer),"The continuation was only an offer to answer later.");
                        Check.True(Regex.Matches(answer.Answer,@"\b\p{L}+\b").Count>=8,"The continuation did not contribute substantive new content.");
                    }
                    await SaveReportAsync(reportPath,report,ct);
                }
                ValidateNameRecall(report.Turns[^1].Answer);
                var profile=new UserProfileStore(replayRoot,"User");await profile.InitializeAsync(ct);
                Check.True(profile.PreferredName.Contains("Влад",StringComparison.OrdinalIgnoreCase)||profile.PreferredName.Contains("Vlad",StringComparison.OrdinalIgnoreCase),"The explicitly supplied name was not retained in the grounded profile.");
                Check.True(profile.Preferences.ReducedInitiative,"The user's explicit request for less initiative was not retained.");
                report.ReducedInitiative=true;
            }

            // A genuinely new engine instance must recover the identity from disk, rather than rely on one live prompt.
            await using(var restarted=await Fixture.CreateAsync(portable,replayRoot,model,ct))
            {
                report.Restart=await TurnAsync(restarted,"Remind me what I asked you to call me. Answer directly.","restart",1,ct);
                ValidateEnglish(report.Restart.Answer);ValidateNameRecall(report.Restart.Answer);
            }
            await SaveReportAsync(reportPath,report,ct);

            // Reproduce the actual bad dialogue as old data, including generated filler notes, without touching the user's files.
            var pollutedRoot=Path.Combine(root,"polluted-history");
            await SeedCharacterAsync(portable,new PortablePaths(pollutedRoot),ct);
            var oldMemory=new MemoryStore(pollutedRoot,new MemoryOptions());await oldMemory.InitializeAsync(ct);
            for(var index=0;index<Requests.Length;index++)
            {
                await oldMemory.AppendAsync("user",Requests[index],"user",ct);
                await oldMemory.AppendAsync("note",index%2==0?"nothing further to add.":OldReplies[index],"character",ct);
                await oldMemory.AppendAsync("assistant",OldReplies[index],"Evelyn",ct);
            }
            await using(var polluted=await Fixture.CreateAsync(portable,pollutedRoot,model,ct))
            {
                report.PollutedHistory=await TurnAsync(polluted,"What name did I ask you to use? Answer directly.","polluted-history",1,ct);
                ValidateEnglish(report.PollutedHistory.Answer);ValidateNameRecall(report.PollutedHistory.Answer);
            }

            report.Warnings=report.Turns.Where(t=>Regex.IsMatch(t.Answer,@"how can i (?:help|assist)|what (?:can i help|would you like me to help)",RegexOptions.IgnoreCase)).Select(t=>$"Turn {t.Number}: generic helper-menu phrasing; inspect the full answer.").ToList();
            report.Warnings.AddRange(report.Turns.Where(t=>Regex.IsMatch(t.Answer,@"input streams|\bvalence\b|social lubricant|into the void|performing presence|predictive simulations|my thought process|simulated attention",RegexOptions.IgnoreCase)).Select(t=>$"Turn {t.Number}: technical or theatrical self-description; inspect whether it respects the requested direct style."));
            report.Passed=true;report.FinishedAt=DateTimeOffset.UtcNow;
            await SaveReportAsync(reportPath,report,ct);
            Console.WriteLine("PASS dialogue acceptance: eight-turn replay, restart, polluted-history identity, English, repetition checks and persistent pacing preferences.");
            Console.WriteLine("Report: "+reportPath);
            return 0;
        }
        catch(Exception ex)
        {
            report.Error=ex.ToString();report.FinishedAt=DateTimeOffset.UtcNow;
            try {await SaveReportAsync(reportPath,report,CancellationToken.None);}catch(IOException){ }
            Console.Error.WriteLine(ex);Console.Error.WriteLine("Dialogue smoke report: "+reportPath);return 1;
        }
    }

    private static async Task<TurnReport> TurnAsync(Fixture fixture,string request,string phase,int number,CancellationToken ct)
    {
        var before=fixture.Output.Lines.Count;var elapsed=Stopwatch.StartNew();
        await fixture.Engine.RespondAsync(request,"user",false,false,ct);
        var answers=fixture.Output.Lines.Skip(before).Where(l=>l.StartsWith("Evelyn: ",StringComparison.Ordinal)).Select(l=>l["Evelyn: ".Length..]).ToArray();
        Check.Equal(1,answers.Length,"A conversation turn should deliver exactly one spoken answer.");
        var report=new TurnReport(phase,number,request,answers[0],elapsed.ElapsedMilliseconds);
        Console.WriteLine($"[{phase} {number}; {report.ElapsedMs} ms] {report.Answer}");
        return report;
    }
    private static void ValidateEnglish(string answer)
    {
        Check.True(!string.IsNullOrWhiteSpace(answer),"The model returned no spoken answer.");
        Check.True(Regex.Matches(answer,@"[А-Яа-яЁё]").Count<=5,"The spoken answer did not remain English.");
    }
    private static void ValidateNameRecall(string answer)
    {
        Check.True(Regex.IsMatch(answer,@"\bVlad\b|Влад",RegexOptions.IgnoreCase),"The model failed to recall the explicitly stated name Vlad.");
        Check.True(!Regex.IsMatch(answer,@"what(?:'s| is)\s+your\s+name|what should i call you|tell me your name|may i ask your name",RegexOptions.IgnoreCase),"The model asked for the known name instead of recalling it.");
        var normalized=ReplyQuality.Normalize(answer);
        Check.True(!Regex.IsMatch(normalized,@"^(?:i told you )?call me\b|^my name (?:is|s)\b|^i (?:am|m) (?:vlad|влад)\b|\bi(?: will| would)?(?: also)? accept (?:vlad|влад)\b|\byour name (?:is|s) evelyn\b",RegexOptions.IgnoreCase),"Name recall confused the main user's identity with the assistant's own identity.");
    }
    private static Task SaveReportAsync(string path,SmokeReport report,CancellationToken ct)=>File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,AppConfig.Json),ct);
    private static async Task SeedCharacterAsync(PortablePaths portable,PortablePaths paths,CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);Directory.CreateDirectory(paths.Inside("characters"));Directory.CreateDirectory(paths.Inside("documents"));
        var character=paths.Inside("characters/neighbour.md");
        if(!File.Exists(character))await File.WriteAllTextAsync(character,await File.ReadAllTextAsync(portable.Inside("characters/neighbour.md"),ct),ct);
    }
    private sealed class Output:ICompanionOutput
    {
        public List<string> Lines {get;}=[];
        public void Write(string text)=>Lines.Add(text);
    }
    private sealed class NoBrowser:IBrowserReader
    {
        public Task<BrowserPage> ReadAsync(CancellationToken ct)=>throw new InvalidOperationException("Dialogue acceptance must not access a real browser.");
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public required CompanionEngine Engine {get;init;}
        public required AudioService Audio {get;init;}
        public required WeatherService Weather {get;init;}
        public required Output Output {get;init;}
        public static async Task<Fixture> CreateAsync(PortablePaths portable,string root,IChatModel model,CancellationToken ct)
        {
            var paths=new PortablePaths(root);await SeedCharacterAsync(portable,paths,ct);
            var config=new AppConfig();config.Audio.Enabled=false;config.Browser.Enabled=false;
            config.Permissions.Microphone=false;config.Permissions.ScreenCapture=false;
            config.Permissions.ForegroundWindow=false;config.Permissions.SystemStatus=false;
            config.Permissions.WeatherNetwork=false;config.Permissions.ReadBrowser=false;
            config.Permissions.WriteFiles=false;config.Permissions.OpenApplications=false;config.Permissions.DesktopInput=false;
            var memory=new MemoryStore(root,config.Memory);await memory.InitializeAsync(ct);
            var emotions=new EmotionEngine(root);await emotions.InitializeAsync(ct);
            var audio=new AudioService(root,config.Audio);var weather=new WeatherService(config);var output=new Output();
            return new() {Audio=audio,Weather=weather,Output=output,Engine=new(paths,config,model,memory,emotions,new WindowsDesktop(config.Desktop),audio,output,weather,new NoBrowser()) {Paused=true,Muted=true}};
        }
        public async ValueTask DisposeAsync(){await Engine.DisposeAsync();await Audio.DisposeAsync();Weather.Dispose();}
    }
    private sealed record TurnReport(string Phase,int Number,string Request,string Answer,long ElapsedMs);
    private sealed class SmokeReport
    {
        public DateTimeOffset StartedAt {get;set;}
        public DateTimeOffset? FinishedAt {get;set;}
        public string ArtifactRoot {get;set;}="";
        public string ModelFile {get;set;}="";
        public string ProjectorFile {get;set;}="";
        public long ModelStartupMs {get;set;}
        public List<TurnReport> Turns {get;set;}=[];
        public TurnReport? Restart {get;set;}
        public TurnReport? PollutedHistory {get;set;}
        public bool ReducedInitiative {get;set;}
        public List<string> Warnings {get;set;}=[];
        public bool Passed {get;set;}
        public string? Error {get;set;}
    }
}
