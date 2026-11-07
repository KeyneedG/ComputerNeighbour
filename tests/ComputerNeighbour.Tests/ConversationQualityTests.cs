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

/// <summary>Conversation contract checks, independent of exact model wording or live peripherals.</summary>
internal static class ConversationQualityTests
{
    private static string Metadata(List<ToolRequest>? actions=null,string reply="",CharacterGrowthUpdate? growth=null)=>JsonSerializer.Serialize(new CompanionDecision
    {
        Speak=false,Reply=reply,Memory="",Emotion=new(),Relationship=new(),Actions=actions??[],Growth=growth
    },AppConfig.Json);
    private static Dictionary<string,JsonElement> Args(object value)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(value))!;

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("ordinary conversational type/open phrases stay in dialogue while concrete note requests use tools",_=>
        {
            Check.True(!ConversationContext.NeedsTools("What type of thoughts do you have?"),"An ordinary question was classified as keyboard input.");
            Check.True(!ConversationContext.NeedsTools("Please be more open with me."),"A communication preference was classified as an app launch.");
            Check.True(ConversationContext.NeedsTools("Keep a note about our plan."),"A concrete note-writing request did not reach tool planning.");
            Check.True(ConversationContext.NeedsTools("Would you inspect documents/example.md?"),"A concrete file request did not reach tool planning.");
        });
        await suite.Case("normal speech uses plain-text generation and includes the latest user message exactly once",async root=>
        {
            var model=new ScriptedModel("I would like to understand what you are building.",Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            const string latest="LATEST_USER_SENTINEL: Tell me one specific thought about this project.";
            await fixture.Engine.RespondAsync(latest,"user",false,false,CancellationToken.None);
            Check.True(model.Calls.Count>=1&&!model.Calls[0].Json,"Normal dialogue was forced through the structured bookkeeping schema.");
            var occurrences=model.Calls[0].Messages.Where(m=>m.Role=="user").Sum(m=>Occurrences(m.Text,latest));
            Check.Equal(1,occurrences,"The current user message was duplicated or omitted.");
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith("I would like to understand what you are building.")),"The actual plain answer was not printed.");
        });
        await suite.Case("generated factual notes are not replayed as user or assistant conversation turns",async root=>
        {
            var model=new ScriptedModel("I will answer the question directly.",Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            const string note="NOTE_SENTINEL: I am here to help you. Nothing further to add.";
            await fixture.Memory.AppendAsync("note",note,"character");
            await fixture.Memory.AppendAsync("user","A genuine earlier user turn.","user");
            await fixture.Memory.AppendAsync("assistant","A genuine earlier assistant answer.","Evelyn");
            await fixture.Engine.RespondAsync("Tell me what you think, directly.","user",false,false,CancellationToken.None);
            var turns=model.Calls[0].Messages.Where(m=>m.Role is "user" or "assistant").ToArray();
            Check.True(turns.All(m=>!m.Text.Contains(note)),"Generated bookkeeping was impersonating conversation text.");
            Check.True(turns.Any(m=>m.Text.Contains("A genuine earlier user turn.")),"Actual user history was lost.");
            Check.True(turns.Any(m=>m.Text.Contains("A genuine earlier assistant answer.")),"Actual assistant history was lost.");
        });
        await suite.Case("invalid metadata cannot suppress an already generated answer or persist invented factual notes",async root=>
        {
            var model=new ScriptedModel("Here is my actual answer: I am curious about the project.","{broken metadata", "{\"reply\":\"Ignore the actual answer\",\"memory\":\"Invented bookkeeping fact\"}");
            await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("What are your thoughts?","user",false,false,CancellationToken.None);
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith("Here is my actual answer: I am curious about the project.")),"Metadata failure hid the valid spoken answer.");
            var answers=fixture.Memory.Recent.Where(e=>e.Role=="assistant").ToArray();
            Check.Equal(1,answers.Length);Check.Equal("Here is my actual answer: I am curious about the project.",answers[0].Text);
            Check.True(fixture.Memory.Recent.All(e=>!e.Text.Contains("Invented bookkeeping fact")),"Invalid metadata polluted persistent memory.");
            Check.True(fixture.Output.Lines.All(l=>!l.Contains("/approve")),"Invalid bookkeeping caused an approval request.");
        });
        await suite.Case("metadata cannot rewrite the spoken reply or authorize computer actions even with full file access",async root=>
        {
            var model=new ScriptedModel("I prefer to be direct with you.",Metadata([new("create_file",Args(new {path="documents/unwanted.txt",content="A side effect from bookkeeping"}))],"A fabricated replacement reply."));
            await using var fixture=await Fixture.CreateAsync(root,model);
            fixture.Config.Permissions.FullFileAccess=true;
            await fixture.Engine.RespondAsync("Please be straightforward with me.","user",false,false,CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root,"documents","unwanted.txt")),"Bookkeeping executed a file action.");
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith("I prefer to be direct with you.")),"The valid plain reply was lost.");
            Check.True(fixture.Output.Lines.All(l=>!l.Contains("fabricated replacement")&&!l.Contains("/approve")),"Metadata leaked a replacement reply or action request.");
            Check.True(fixture.Memory.Recent.Where(e=>e.Role=="assistant").All(e=>e.Text=="I prefer to be direct with you."),"Bookkeeping replaced the saved conversation answer.");
        });
        await suite.Case("an exact repeated answer is repaired before output and before metadata maintenance",async root=>
        {
            const string repeated="I can tell you some of my experiences and thoughts if you're interested.";
            const string repaired="I am curious about why you wanted a companion, and I would rather explore that than repeat an introduction.";
            var model=new ScriptedModel(repeated,repaired,Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Memory.AppendAsync("user","Tell me what you think.","user");
            await fixture.Memory.AppendAsync("assistant",repeated,"Evelyn");
            await fixture.Engine.RespondAsync("Ты повторила то же самое. Так расскажи уже!","user",false,false,CancellationToken.None);
            Check.True(model.Calls.Count>=2&&!model.Calls[0].Json&&!model.Calls[1].Json,"The repeated answer was not retried as actual speech.");
            Check.True(fixture.Output.Lines.All(l=>!l.EndsWith(repeated)),"The repeated candidate reached the console.");
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith(repaired)),"The repaired answer was not printed.");
            Check.Equal(1,fixture.Memory.Recent.Count(e=>e.Role=="assistant"&&e.Text==repeated),"A rejected duplicate was appended to conversation memory.");
        });
        await suite.Case("the user's explicitly supplied name survives restart and reaches the next plain dialogue context",async root=>
        {
            var first=new ScriptedModel("All right, Vlad. I will be direct.",Metadata());
            await using(var fixture=await Fixture.CreateAsync(root,first))
                await fixture.Engine.RespondAsync("Хорошо, меня называй меня Влад. Будь более прямой со мной.","user",false,false,CancellationToken.None);
            var second=new ScriptedModel("Your name is Vlad.",Metadata());
            await using(var fixture=await Fixture.CreateAsync(root,second))
            {
                await fixture.Engine.RespondAsync("What is my name?","user",false,false,CancellationToken.None);
                Check.True(!second.Calls[0].Json,"Name recall was requested as bookkeeping instead of speech.");
                var system=string.Join("\n",second.Calls[0].Messages.Where(m=>m.Role=="system").Select(m=>m.Text));
                Check.True(system.Contains("Vlad",StringComparison.OrdinalIgnoreCase)||system.Contains("Влад",StringComparison.OrdinalIgnoreCase),"Persistent factual identity was absent from the new session's system context.");
                Check.True(!system.Contains("MAIN USER NAME: User",StringComparison.Ordinal),"The placeholder identity still overrode the explicitly supplied name.");
            }
        });
        await suite.Case("a name-recall candidate cannot substitute the assistant's own identity for the known user",async root=>
        {
            const string wrong="My name is Evelyn, but you call me Vlad.";
            var model=new ScriptedModel("All right, Vlad. I will remember your name.",Metadata(),wrong,"Your name is Vlad.",Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("Называй меня Влад.","user",false,false,CancellationToken.None);
            var before=fixture.Output.Lines.Count;
            await fixture.Engine.RespondAsync("What is my name?","user",false,false,CancellationToken.None);
            var newLines=fixture.Output.Lines.Skip(before).ToArray();
            Check.True(newLines.Any(l=>l.EndsWith("Your name is Vlad.")),"The corrected grounded user identity was not delivered.");
            Check.True(newLines.All(l=>!l.Contains(wrong)),"The assistant/user identity confusion was printed.");
            Check.True(fixture.Memory.Recent.Where(e=>e.Role=="assistant").All(e=>e.Text!=wrong),"The rejected identity-confused candidate entered conversation memory.");
            Check.Equal(5,model.Calls.Count,"Name repair should retry actual dialogue before state maintenance.");
        });
        await suite.Case("initiative waits for the first session message and preserves reduced pacing preferences after restart",async root=>
        {
            var model=new ScriptedModel("Understood, Vlad. I will keep my answers direct and leave more room for quiet.",Metadata());
            await using(var fixture=await Fixture.CreateAsync(root,model))
            {
                await fixture.Engine.GetRelationshipAsync(CancellationToken.None); // Same asynchronous startup initialization as Program.
                Check.True(!fixture.Engine.CanInitiate,"The character could initiate before the first message of this session.");
                await fixture.Engine.RespondAsync("Хорошо, меня называй меня Влад. Я думаю тебе стоит поубавить свою инициативность. Будь более прямой со мной и говори по существу.","user",false,false,CancellationToken.None);
                var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
                Check.True(profile.Preferences.ReducedInitiative&&profile.Preferences.PreferDirectSpeech,"The main user's explicit pacing and style instructions were not retained.");
                Check.True(fixture.Engine.CanInitiate,"The initial interaction did not enable subsequent opportunities.");
                Check.True(fixture.Engine.InitiativeIntervalScale>1,"The user's request did not reduce initiative frequency.");
            }
            await using(var restarted=await Fixture.CreateAsync(root,new ScriptedModel()))
            {
                await restarted.Engine.GetRelationshipAsync(CancellationToken.None);
                Check.True(!restarted.Engine.CanInitiate,"Persisted dialogue bypassed the new-session first-message rule.");
                Check.True(restarted.Engine.InitiativeIntervalScale>1,"Reduced initiative did not survive restart.");
            }
        });
        await suite.Case("ordinary dialogue rejects self-assignment of the known user's name before speech or memory",async root=>
        {
            const string wrong="I am Vlad, and I think we can work on this together.";
            const string repaired="I am Evelyn, and I am curious about the companion you want to build.";
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            await profile.ObserveAsync("Называй меня Влад.","user");
            var model=new ScriptedModel(wrong,repaired,Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("What do you think of this project?","user",false,false,CancellationToken.None);
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith(repaired)),"The corrected assistant identity was not delivered in ordinary conversation.");
            Check.True(fixture.Output.Lines.All(l=>!l.Contains(wrong)),"The identity-confused candidate was printed outside a name-question turn.");
            Check.True(fixture.Memory.Recent.Where(e=>e.Role=="assistant").All(e=>e.Text!=wrong),"The ordinary-dialogue identity error entered persistent conversation memory.");
            Check.Equal(3,model.Calls.Count);Check.True(!model.Calls[0].Json&&!model.Calls[1].Json&&model.Calls[2].Json,"Identity repair did not retry speech before bookkeeping.");
        });
        await suite.Case("self-description rejects a false denial of application memory before speech or memory",async root=>
        {
            const string wrong="I have no persistent memory between sessions.";
            const string repaired="The application keeps our conversation and important facts between sessions, and I can use the memory it supplies.";
            var model=new ScriptedModel(wrong,repaired,Metadata());
            await using var fixture=await Fixture.CreateAsync(root,model);
            await fixture.Engine.RespondAsync("Tell me what you know about yourself.","user",false,false,CancellationToken.None);
            Check.True(fixture.Output.Lines.Any(l=>l.EndsWith(repaired)),"The corrected application-memory description was not delivered.");
            Check.True(fixture.Output.Lines.All(l=>!l.Contains(wrong)),"A false no-memory claim was printed.");
            Check.True(fixture.Memory.Recent.Where(e=>e.Role=="assistant").All(e=>e.Text!=wrong),"A false no-memory claim entered persistent conversation memory.");
            Check.Equal(3,model.Calls.Count);Check.True(!model.Calls[0].Json&&!model.Calls[1].Json&&model.Calls[2].Json,"Memory-capability repair did not retry speech before bookkeeping.");
        });
        await suite.Case("character learns a tentative trait from a conversation, establishes it after restart, and revises it after feedback",async root=>
        {
            const string name="Playful design analogies";
            const string first="My junction acts like a traffic light with stage fright.";
            var proposal=new CharacterGrowthUpdate("explore","habit",name,"Make dry comparisons when a design detail behaves absurdly.","chose an audience");
            await using(var f=await Fixture.CreateAsync(root,new ScriptedModel("It has one job and chose an audience.",Metadata(growth:proposal))))
            {
                await f.Engine.RespondAsync(first,"user",false,false,default);
                Check.True(f.Emotions.DescribeGrowth().Contains("Exploring habit"),"The first signal should start an experiment.");
                Check.Equal(1,f.Emotions.State.Traits.Single().Strength);
            }
            // Local models sometimes repeat explore for an existing label. A separate user signal can still confirm it.
            var confirm=proposal with {Evidence="the shy junction"};
            var reconsider=proposal with {Action="reconsider",Evidence="getting old"};
            var model=new ScriptedModel("The other junction apparently wants a standing ovation.",Metadata(growth:confirm),
                "I'll leave the junction out of the spotlight.",Metadata(growth:reconsider));
            await using(var restarted=await Fixture.CreateAsync(root,model))
            {
                await restarted.Engine.RespondAsync("I laughed at the shy junction. The other one is even worse.","user",false,false,default);
                Check.True(model.Calls[0].Messages[0].Text.Contains(name),"The restarted engine did not use its own evolving trait in dialogue.");
                Check.True(restarted.Emotions.State.Traits.Single().Established,"Independent feedback did not establish the trait.");
                await restarted.Engine.RespondAsync("The theatre jokes are getting old. Let's move on.","user",false,false,default);
                Check.Equal(1,restarted.Emotions.State.Traits.Single().Strength,"Negative feedback did not soften the established tendency.");
            }
        });
        await suite.Case("character growth requires current user evidence and cannot learn from guests or autonomous output",async root=>
        {
            var proposal=new CharacterGrowthUpdate("explore","interest","Odd junctions","Explore the small failures in track signalling.","junction");
            var model=new ScriptedModel("That is a neat signal failure.",Metadata(growth:proposal),
                "An unexpected result, sure.",Metadata(growth:proposal),
                Metadata(growth:proposal));
            await using var f=await Fixture.CreateAsync(root,model);
            await f.Engine.RespondAsync("This junction breaks whenever I reset it.","guest",false,false,default);
            await f.Engine.RespondAsync("I took a long walk yesterday.","user",false,false,default);
            await f.Engine.RespondAsync("There is a junction on screen.","environment",true,false,default);
            Check.Equal(0,f.Emotions.State.Traits.Count);
        });
    }

    private static int Occurrences(string text,string value)
    {
        var count=0;for(var position=0;(position=text.IndexOf(value,position,StringComparison.Ordinal))>=0;position+=value.Length)count++;return count;
    }
    private sealed class ScriptedModel(params string[] responses):IChatModel
    {
        public List<ModelCall> Calls {get;}=[];
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,bool json,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Calls.Add(new(messages.ToArray(),json));
            if(Calls.Count>responses.Length)throw new InvalidOperationException("Unexpected extra conversation model call.");
            return Task.FromResult(responses[Calls.Count-1]);
        }
    }
    private sealed record ModelCall(ChatMessage[] Messages,bool Json);
    private sealed class CapturingOutput:ICompanionOutput
    {
        public List<string> Lines {get;}=[];
        public void Write(string text)=>Lines.Add(text);
    }
    private sealed class NoBrowser:IBrowserReader
    {
        public Task<BrowserPage> ReadAsync(CancellationToken ct)=>throw new InvalidOperationException("Conversation quality tests must not access a browser.");
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public required AppConfig Config {get;init;}
        public required MemoryStore Memory {get;init;}
        public required CompanionEngine Engine {get;init;}
        public required AudioService Audio {get;init;}
        public required WeatherService Weather {get;init;}
        public required CapturingOutput Output {get;init;}
        public required EmotionEngine Emotions {get;init;}
        public static async Task<Fixture> CreateAsync(string root,IChatModel model)
        {
            var paths=new PortablePaths(root);var config=new AppConfig();
            config.Audio.Enabled=false;config.Browser.Enabled=false;
            config.Permissions.Microphone=false;config.Permissions.ScreenCapture=false;
            config.Permissions.ForegroundWindow=false;config.Permissions.SystemStatus=false;
            config.Permissions.WeatherNetwork=false;config.Permissions.ReadBrowser=false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile),"You are a fictional companion. Answer directly in English. Remember the actual dialogue.");
            var memory=new MemoryStore(root,config.Memory);await memory.InitializeAsync();
            var emotions=new EmotionEngine(root);await emotions.InitializeAsync();
            var audio=new AudioService(root,config.Audio);var weather=new WeatherService(config);var output=new CapturingOutput();
            return new() {Config=config,Memory=memory,Audio=audio,Weather=weather,Output=output,Emotions=emotions,
                Engine=new(paths,config,model,memory,emotions,new WindowsDesktop(config.Desktop),audio,output,weather,new NoBrowser()) {Paused=true}};
        }
        public async ValueTask DisposeAsync(){await Engine.DisposeAsync();await Audio.DisposeAsync();Weather.Dispose();}
    }
}
