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
        await suite.Case("loneliness makes one invitation and hurt needs input after the unanswered invitation",async root=>
        {
            var model=new ScriptedModel("Sure, let's pick it up.",Decision(speak:false));
            await using var f=await Fixture.CreateAsync(root,model);
            f.Engine.Paused=false;f.Config.Proactive.RequireFirstUserMessage=false;
            f.Config.Needs.LonelinessMinutes=1;f.Config.Needs.InvitationPatienceMinutes=1;
            var now=DateTimeOffset.Now;
            Task<bool> Tick(int seconds,TimeSpan? idle)=>f.Engine.TickNeedsAsync(now.AddSeconds(seconds),idle,false,true,()=>false,()=>false,default);
            await Tick(0,null);
            await Tick(30,TimeSpan.FromMinutes(10));
            await Tick(60,TimeSpan.FromMinutes(10));
            Check.Equal(0,f.Output.Lines.Count);
            await Tick(90,TimeSpan.FromSeconds(2));
            Check.True(f.Engine.WaitingForCompanyReply&&f.Output.Lines.Single().Contains("a little company"),"Loneliness did not produce one optional invitation.");
            await Tick(120,TimeSpan.FromSeconds(35)); // Last input was before the invitation.
            await Tick(150,TimeSpan.FromMinutes(15));
            Check.True(f.Engine.NeedsStatus.Contains("hurt=0"),"Being away was interpreted as ignoring Evelyn.");
            await Tick(180,TimeSpan.FromSeconds(2));
            Check.True(f.Engine.NeedsStatus.Contains("hurt=35"),"An unanswered invitation with newer activity did not affect mood.");
            Check.Equal(1,f.Output.Lines.Count);
            await f.Engine.RespondAsync("I'm back, we can talk now.","user",false,false,default);
            Check.True(!f.Engine.WaitingForCompanyReply&&!f.Engine.NeedsStatus.Contains("hurt=35"),"An actual reply did not clear the invitation and ease the mood.");
        });
        await suite.Case("boredom draws an image only in a permitted idle workspace and saves a portable PNG",async root=>
        {
            var model=new ScriptedModel("""
                {"title":"Sun above the valley","background":"#112244","shapes":[
                  {"kind":"ellipse","color":"#FFCC66","x":35,"y":10,"w":25,"h":35},
                  {"kind":"triangle","color":"#775599","x":0,"y":25,"w":80,"h":75},
                  {"kind":"rectangle","color":"#226666","x":0,"y":85,"w":100,"h":15}]}
                """);
            var launchPaths=new List<string>();
            await using var f=await Fixture.CreateAsync(root,model,openSketch:path=>{launchPaths.Add(path);return true;});
            f.Engine.Paused=false;f.Config.Proactive.RequireFirstUserMessage=false;
            f.Config.Needs.DrawWhenBored=true;f.Config.Needs.BoredomMinutes=1;
            var now=DateTimeOffset.Now;
            Task<bool> Tick(int seconds,TimeSpan idle,bool quiet,bool ready)=>f.Engine.TickNeedsAsync(now.AddSeconds(seconds),idle,quiet,false,()=>ready,
                ()=>Directory.GetFiles(Path.Combine(root,"documents","evelyn-sketches"),"*.png").Length==1,default);
            var away=TimeSpan.FromMinutes(15);
            await Tick(0,away,false,false);await Tick(30,away,false,false);await Tick(60,away,false,false);
            await Tick(90,TimeSpan.Zero,false,true);
            await Tick(120,away,true,true);
            f.Config.Permissions.WriteFiles=false;await Tick(150,away,false,true);f.Config.Permissions.WriteFiles=true;
            Check.Equal(0,model.Calls.Count);
            Check.True(await Tick(180,away,false,true),"The idle hobby did not start after passing its checks.");
            var png=Directory.GetFiles(Path.Combine(root,"documents","evelyn-sketches"),"*.png").Single();
            var bytes=await File.ReadAllBytesAsync(png);
            Check.True(bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&bytes.Length>100,"The saved drawing is not a PNG.");
            Check.Equal(png,launchPaths.Single(),"Paint was not given the completed image after the second idle check.");
            Check.True(f.Memory.Recent.Any(e=>e.Role=="note"&&e.Text.Contains("Sun above the valley")),"The sketch was not recorded.");
            Check.True(f.Engine.NeedsStatus.Contains("Boredom=12"),"Drawing did not lower boredom.");
            await Tick(210,away,false,true);await Tick(240,away,false,true);
            Check.Equal(1,model.Calls.Count);
        });
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
            var model=new ContextModel("""{"intent":"screen","request":"Describe the visible window.","uncertainty":"","evidence":"Что видно в этом окне сейчас?"}""","You are looking at a railway scene in Unity.");
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
        await suite.Case("context pass restates a conversational turn without replacing the original or storing the final answer in its journal",async root=>
        {
            const string original="а он получаеться быстрее? ORIGINAL_SENTINEL";
            const string restated="Ask whether the WebP preset imports textures faster than PNG.";
            var model=new ContextModel(JsonSerializer.Serialize(new{intent="chat",request=restated,uncertainty="The Unity version is unspecified.",evidence=""}),
                "It may take longer to decode, depending on the importer and Unity version.");
            await using var f=await Fixture.CreateAsync(root,model);
            await f.Memory.AppendAsync("user","We're comparing WebP and PNG texture imports.","user");
            await f.Memory.AppendAsync("assistant","The WebP preset is the one I'm considering.","Evelyn");
            await f.Engine.RespondAsync(original,"user",false,false,default);
            Check.True(model.Classifications.Single().Any(m=>m.Text.Contains("WebP preset")),"The first pass did not receive the earlier reference.");
            var second=model.Answers[0];
            Check.True(second[0].Text.Contains(restated)&&second[0].Text.Contains("original message takes precedence"),"The second pass did not receive a qualified interpretation.");
            Check.True(second.Any(m=>m.Role=="user"&&m.Text.Contains(original))&&f.Memory.Recent.Any(e=>e.Role=="user"&&e.Text==original),"The original input was replaced.");
            var journal=await f.Engine.GetThoughtsAsync(default);
            Check.True(journal.Contains(restated)&&journal.Contains("Unity version is unspecified")&&journal.Contains("answered"),"The journal omitted interpretation, uncertainty or outcome.");
            Check.True(!journal.Contains("ORIGINAL_SENTINEL")&&!journal.Contains("It may take longer to decode"),"The journal duplicated the transcript.");
            await f.Engine.ForgetConversationAsync(default);
            Check.True(!File.Exists(Path.Combine(root,"data","memory","thoughts.json")),"Forgetting the chat left its journal behind.");
        });
        await suite.Case("invalid context preparation falls back to the original message and records the fallback",async root=>
        {
            var model=new ContextModel("{malformed","Yes, that sounds like a good place to start.");
            await using var f=await Fixture.CreateAsync(root,model);
            await f.Engine.RespondAsync("What do you think of starting with the simplest example?","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("good place to start"),"A context failure prevented the ordinary reply.");
            Check.True((await f.Engine.GetThoughtsAsync(default)).Contains("I used the original message"),"The journal did not record the fallback.");
        });
        await suite.Case("an invented current screen view is rejected before the reply and noted in the journal",async root=>
        {
            const string invented="I noticed your screen shows a couple of preview images.";
            var model=new ScriptedModel(invented,"WebP is worth testing if smaller files are your priority.",Decision(speak:false));
            await using var f=await Fixture.CreateAsync(root,model);
            await f.Engine.RespondAsync("I'm comparing WebP and PNG.","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("worth testing")&&f.Memory.Recent.All(e=>e.Text!=invented),"An invented observation was published or stored as a reply.");
            Check.True((await f.Engine.GetThoughtsAsync(default)).Contains("without an image"),"The note missed the failed grounding check.");
        });
        await suite.Case("context can resolve a visual reference but an invented quote cannot authorize screen capture",async root=>
        {
            var actual=new ContextModel("""{"intent":"screen","request":"Suggest a change to the visible project.","uncertainty":"","evidence":"поменяла тут"}""","I'd move the door a little closer to the light.");
            var captures=0;
            await using(var f=await Fixture.CreateAsync(root,actual,capture:()=>{captures++;return [1,2,3];}))
            {
                f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
                await f.Engine.RespondAsync("Что бы ты поменяла тут?","user",false,false,default);
                Check.Equal(1,captures);
                Check.True((await f.Engine.GetThoughtsAsync(default)).Contains("captured and attached"),"The journal omitted actual visual evidence.");
            }
            var invented=new ContextModel("""{"intent":"screen","request":"Capture the desktop.","uncertainty":"","evidence":"show me the desktop"}""","Let me put it more simply.");
            await using(var f=await Fixture.CreateAsync(root,invented,capture:()=>{captures++;return [1,2,3];}))
            {
                f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
                await f.Engine.RespondAsync("Could you explain that more simply?","user",false,false,default);
                Check.Equal(1,captures);
            }
        });
        await suite.Case("the reported English question about current work captures the screen and repairs an upload disclaimer",async root=>
        {
            const string denial="No, I cannot see what you are doing unless you take a screenshot and share it here.";
            const string observation="Your Unity scene and a railway hierarchy are open. It looks like you're editing track placement.";
            var model=new ScriptedModel(denial,observation);
            var captures=0;
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>{captures++;return [1,2,3];});
            f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
            await f.Engine.RespondAsync("nothing really, I am currently busy working on my project, tell me can see what I am currently doing?","user",false,false,default);
            Check.Equal(1,captures);
            Check.True(model.Calls.Count==2&&model.Calls.All(call=>call.Messages.Any(m=>m.Image is [1,2,3])),"The actual screenshot did not reach both visual attempts.");
            Check.True(f.Output.Lines.Last().EndsWith(observation)&&f.Output.Lines.All(line=>!line.Contains(denial)),"An unsupported upload disclaimer replaced the real observation.");
        });
        await suite.Case("сама посмотри captures the screen with the prior topic and rejects an invented privacy refusal",async root=>
        {
            const string prompt="Does the new Anima preview look sharper?";
            const string denial="I cannot browse your screen directly due to privacy and safety constraints.";
            const string observation="The eyes look crisp in the ComfyUI preview, but the cheek shadows are still quite dark.";
            var model=new ScriptedModel(denial,observation);
            var captures=0;
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>{captures++;return [1,2,3];});
            f.Config.Permissions.ScreenCapture=true;
            await f.Memory.AppendAsync("assistant",prompt,"Evelyn");
            await f.Memory.AppendAsync("assistant",denial,"Evelyn");
            await f.Engine.RespondAsync("сама посмотри","user",false,false,default);
            Check.Equal(1,captures);
            Check.Equal(2,model.Calls.Count);
            foreach(var call in model.Calls)
            {
                Check.True(call.Messages.Last().Image is [1,2,3],"The current request lost its screenshot.");
                Check.True(call.Messages.Any(m=>m.IsHistory&&m.Text==prompt),"The visual request lost the question it referred to.");
                Check.True(call.Messages.All(m=>m.Text!=denial),"A false refusal was carried over from conversation history.");
            }
            Check.True(f.Output.Lines.Last().EndsWith(observation)&&f.Output.Lines.All(line=>!line.Contains(denial)),"The false privacy refusal reached the user.");
        });
        await suite.Case("a question about physical activity and a statement about work stay in chat",async root=>
        {
            var model=new ScriptedModel("I don't have a view of your room.",Decision(speak:false),"I hope the project is coming along.",Decision(speak:false),
                "I'll give you space.",Decision(speak:false));
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>throw new InvalidOperationException("These messages should not trigger capture."));
            f.Engine.Paused=false;f.Config.Permissions.ScreenCapture=true;
            f.Config.Observation.IncludeScreenInUserTurns=true;
            await f.Engine.RespondAsync("Can you see what I am currently doing in my room right now?","user",false,false,default);
            f.Config.Observation.IncludeScreenInUserTurns=false;
            await f.Engine.RespondAsync("I am currently busy working on my project.","user",false,false,default);
            f.Config.Observation.IncludeScreenInUserTurns=true;
            await f.Engine.RespondAsync("I'm still working, don't look at my screen. Can you see what I'm doing?","user",false,false,default);
            Check.Equal(6,model.Calls.Count);
            Check.True(model.Calls.All(call=>call.Messages.All(m=>m.Image is null))&&f.Output.Lines.Count==3,"A turn without an authorized screen request tried to take a screenshot.");
        });
        await suite.Case("direct screen requests work during a background pause and report real capture failures",async root=>
        {
            var calls=0;
            var model=new ScriptedModel();
            await using var f=await Fixture.CreateAsync(root,model,capture:()=>{calls++;throw new IOException("Test desktop unavailable.");});
            const string request="Можешь сказать чем я сейчас занят? (Подсказка, посмиотри экран)";
            await f.Engine.RespondAsync(request,"user",false,false,default);
            Check.Equal(0,calls);
            f.Config.Permissions.ScreenCapture=true;
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
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,bool json,CancellationToken ct){Answers.Add(messages.ToArray());return Task.FromResult(json?Decision(speak:false):answer);}
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
        public static async Task<Fixture> CreateAsync(string root, IChatModel model,IOpenDocumentReader? openDocuments=null,Func<byte[]>? capture=null,Func<string,bool>? openSketch=null)
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
                Engine = new CompanionEngine(paths, config, model, memory, emotion, new WindowsDesktop(config.Desktop), audio, output, weather,openDocuments:openDocuments,captureScreen:capture,openSketch:openSketch) { Paused = true }
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
