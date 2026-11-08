using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Tests;

internal static class AutonomyTests
{
    internal static async Task RunAsync(Suite suite)
    {
        await suite.Case("personal goals prefer rest when tired and replan with known facts and available actions", _ =>
        {
            var tired = new NeedLevels { Energy = .18, Fatigue = .9, Curiosity = 1, Inspiration = 1 };
            Check.Equal<PersonalGoal?>(PersonalGoal.Rest, AutonomyPlanner.Choose(tired, null, _ => true, () => 0));
            var lonely = new NeedLevels { Loneliness = 1 };
            Check.Equal<PersonalGoal?>(PersonalGoal.Company, AutonomyPlanner.Choose(lonely, null,
                goal => goal != PersonalGoal.Train && goal != PersonalGoal.Recover, () => 0));
            var google = AutonomyPlanner.Plan(PersonalGoal.Company, 0, lonely, _ => true);
            Check.Equal("Plan,AskGoogle,Reflect,Review,SaveNote", string.Join(',', google));
            var fallback = AutonomyPlanner.Plan(PersonalGoal.Company, PersonalFact.FoundSource, lonely,
                action => action != PersonalAction.AskGoogle);
            Check.Equal("Plan,ReadArticle,Reflect,Review,SaveNote", string.Join(',', fallback));
            var inspired = new NeedLevels { Inspiration = 1, Energy = 1, Playfulness = 1 };
            Check.Equal<PersonalGoal?>(PersonalGoal.Explore, AutonomyPlanner.Choose(inspired, PersonalGoal.Read,
                goal => goal is PersonalGoal.Explore or PersonalGoal.Write, () => 0,
                [PersonalGoal.Write, PersonalGoal.Practice, PersonalGoal.Read]));
        });

        await suite.Case("an interrupted personal lookup preserves its goal across restart and keeps its diary separate", async root =>
        {
            var now = DateTimeOffset.UtcNow;
            var paths = new PortablePaths(root);
            var goal = new PersonalGoalState(PersonalGoal.Explore, Guid.NewGuid(), now, "Cyanotypes")
                { Facts = PersonalFact.Planned };
            await SeedAsync(paths, goal);
            var web = new FakeWeb { BlockSearch = true };
            await using (var run = await Fixture.OpenAsync(paths, web))
            {
                await run.Memory.AppendAsync("user", "Private dialogue 8e164b", "user");
                var skipped = await run.Engine.RunBackgroundAsync(ct => Tick(run.Engine, now, ct), () => true, default);
                Check.True(!skipped && web.Queries.Count == 0, "A queued input did not win against a background tick.");
                var background = run.Engine.RunBackgroundAsync(ct => Tick(run.Engine, now, ct), () => false, default);
                await web.SearchStarted.Task;
                run.Engine.InterruptActivity();
                await background;
                var checkpoint = await ReadLifeAsync(paths);
                Check.Equal(goal.Id, checkpoint.Active?.Id);
                Check.Equal(0, checkpoint.Active!.Failed.Length);
            }
            web.BlockSearch = false;
            await using var reopened = await Fixture.OpenAsync(paths, web);
            Check.True(!reopened.Engine.CanInitiate, "This test must run without a first message in the new session.");
            await StepsAsync(reopened.Engine, now.AddMinutes(1), 5);
            var result = await ReadLifeAsync(paths);
            Check.Equal<PersonalGoalState?>(null, result.Active);
            Check.Equal(PersonalGoal.Explore, result.Journal.Single().Goal);
            Check.True((await reopened.Engine.ReadDiaryAsync(default)).Contains("cyanotype"), "The completed reflection is missing.");
            Check.True(reopened.Memory.Recent.All(entry => entry.Role != "assistant"), "Private thoughts became spoken dialogue.");
            Check.True(web.Queries.All(query => !query.Contains("8e164b")), "Private dialogue was sent to the search provider.");
        });

        await suite.Case("exhaustion checkpoints sleep and restores the same unfinished goal", async root =>
        {
            var now = DateTimeOffset.UtcNow;
            var paths = new PortablePaths(root);
            var goal = new PersonalGoalState(PersonalGoal.Write, Guid.NewGuid(), now, "Tiny stories");
            await SeedAsync(paths, goal);
            await using var run = await Fixture.OpenAsync(paths, new FakeWeb(), initial: new() { Energy = .13, Fatigue = .92 });
            await Tick(run.Engine, now);
            var sleeping = await ReadLifeAsync(paths);
            Check.Equal(PersonalGoal.Rest, sleeping.Active?.Goal);
            Check.Equal(goal.Id, sleeping.Suspended?.Id);
            Check.True(run.Engine.IsResting, "Sleep was not exposed as the active goal.");
            await Tick(run.Engine, sleeping.Active!.WakeAt!.Value.AddSeconds(1));
            Check.Equal(goal.Id, (await ReadLifeAsync(paths)).Active?.Id);
            Check.True(run.Engine.NeedsStatus.Contains("energy=86"), "Rest did not replenish energy.");
        });

        await suite.Case("only direct owner requests defer goals and an explicit pause survives restart", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            var goal = new PersonalGoalState(PersonalGoal.Write, Guid.NewGuid(), now, "Tiny stories");
            await SeedAsync(paths, goal);
            await using (var run = await Fixture.OpenAsync(paths, new FakeWeb()))
            {
                await run.Engine.RespondAsync("Эвелин, приубавь пыл", "guest", false, false, default);
                await run.Engine.RespondAsync("Она сказала «приубавь пыл».", "user", false, false, default);
                Check.True(!run.Engine.BackgroundDeferred(DateTimeOffset.UtcNow), "A guest or quotation changed the schedule.");
                await run.Engine.RespondAsync("Эвелин, можешь немного приубавить свой пыл?", "user", false, false, default);
                Check.True(run.Engine.BackgroundDeferred(DateTimeOffset.UtcNow), "A direct Russian request did not defer the goal.");
                Check.Equal(goal.Id, (await ReadLifeAsync(paths)).Active?.Id);
                await run.Engine.SetBackgroundPauseAsync(true, default);
                await using var incognito = await run.Engine.ForkIncognitoAsync(default);
                Check.True(!incognito.AutonomyEnabled, "Incognito can access personal activity.");
                await incognito.SetBackgroundPauseAsync(false, default);
                Check.True((await ReadLifeAsync(paths)).Paused, "Incognito changed the regular pause.");
            }
            await using var reopened = await Fixture.OpenAsync(paths, new FakeWeb());
            Check.True(reopened.Engine.Paused, "The pause did not survive restart.");
            await reopened.Engine.SetBackgroundPauseAsync(false, default);
            await StepsAsync(reopened.Engine, now.AddMinutes(1), 7);
            var result = await ReadLifeAsync(paths);
            Check.Equal(PersonalGoal.Write, result.Journal.Single().Goal);
            Check.True(File.Exists(paths.Inside(result.Journal.Single().Reference)), "The resumed writing goal did not create a real file.");
        });

        await suite.Case("Google failure falls back to an article with no private material in the question", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            var web = new FakeWeb();
            string? question = null;
            await SeedAsync(paths, new PersonalGoalState(PersonalGoal.Company, Guid.NewGuid(), now, "Cyanotypes")
                { Facts = PersonalFact.Planned });
            await using var run = await Fixture.OpenAsync(paths, web, google: (text, _) =>
            {
                question = text;
                throw new IOException("AI Mode was unavailable.");
            });
            await run.Memory.AppendAsync("user", "Private detail 7a9", "user");
            await run.Engine.TryHandleAccessCommandAsync("/desktop on", default);
            await Tick(run.Engine, now, idle: TimeSpan.FromMinutes(8));
            Check.True(question?.Contains("Anna Atkins") == true && !question.Contains("7a9"), "Google did not get a scoped public question.");
            Check.True((await ReadLifeAsync(paths)).Active!.Failed.Contains(PersonalAction.AskGoogle), "The failure did not reach the planner.");
            await StepsAsync(run.Engine, now.AddMinutes(1), 5);
            Check.Equal(FakeWeb.Source, (await ReadLifeAsync(paths)).Journal.Single().Reference);
        });

        await suite.Case("a failed public read can become a study goal and retry the original permitted URL", async root =>
        {
            var paths = new PortablePaths(root);
            var web = new FakeWeb { FailOriginal = true };
            await using var run = await Fixture.OpenAsync(paths, web);
            await run.Engine.RespondAsync("Read " + FakeWeb.Original, "user", false, false, default);
            Check.True(run.Output.Lines.Any(line => line.Contains("couldn't complete that lookup")), "The immediate reply hid the failed attempt.");
            var store = new PersonalLife(paths);
            await store.LoadAsync(default);
            Check.Equal(FakeWeb.Original, store.State.Backlog.Single().Goal.Challenge?.Target);
            var now = DateTimeOffset.UtcNow;
            await using var reopened = await Fixture.OpenAsync(paths, web);
            await StepsAsync(reopened.Engine, now, 6);
            var done = await ReadLifeAsync(paths);
            Check.True(done.Backlog.Length == 0 && done.Notice?.Text.Contains("managed to read") == true, "No verified recovery was recorded.");
            Check.Equal(2, web.Reads.Count(url => url == FakeWeb.Original));
        });

        await suite.Case("a story keeps three scenes, waits for its targeted revision, and later extends the same titled file", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            var goal = new PersonalGoalState(PersonalGoal.Write, Guid.NewGuid(), now, "Tiny stories");
            await SeedAsync(paths, goal);
            var model = new TestModel("revise", "accept", "accept");
            await using (var run = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model))
            {
                await StepsAsync(run.Engine, now, 2);
                var first = (await ReadLifeAsync(paths)).Active!;
                Check.Equal(goal.Id, first.Id);
                Check.Equal(1, first.Project.Scenes.Length);
                Check.True(model.OpeningContext.Contains("behind the closed ferry office") &&
                    !model.OpeningContext.Contains("The ferry answers"), "The opening prompt exposed the ending.");
                Check.True(!Directory.Exists(paths.Inside(PersonalWriting.Folder)), "An unfinished first scene was published.");
            }
            string story;
            string reference;
            DateTimeOffset due;
            await using (var run = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model))
            {
                await StepsAsync(run.Engine, now.AddMinutes(2), 3);
                var reviewed = (await ReadLifeAsync(paths)).Active!;
                Check.Equal(3, reviewed.Project.Scenes.Length);
                Check.Equal("revise", reviewed.Project.Review?.Decision);
                Check.True(!Directory.Exists(paths.Inside(PersonalWriting.Folder)), "The rejected draft was published.");
                await StepsAsync(run.Engine, now.AddMinutes(5), 4);
                var complete = await ReadLifeAsync(paths);
                reference = complete.Journal.Single().Reference;
                Check.Equal(PersonalWriting.Folder + "/The Bell Under Glass.md", reference);
                story = await File.ReadAllTextAsync(paths.Inside(reference));
                Check.True(story.Contains("changed the price of her choice") && story.Contains("the ferry arrived"), "The targeted revision or ending was lost.");
                var revisit = complete.Backlog.Single();
                Check.Equal(goal.Id, revisit.Goal.Id);
                Check.Equal(reference, revisit.Goal.Reference);
                Check.True(run.Engine.DescribePersonalLife(now).Contains("Reread the story with fresh eyes"), "The follow-up wasn't visible.");
                due = revisit.Due;
            }
            await using var reopened = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model);
            await StepsAsync(reopened.Engine, due, 6);
            var extended = await File.ReadAllTextAsync(paths.Inside(reference));
            Check.True(extended.StartsWith(story.TrimEnd(), StringComparison.Ordinal) && extended.Contains("the apprentice returned"), "The revisit did not extend the same story.");
            Check.Equal(1, Directory.GetFiles(paths.Inside(PersonalWriting.Folder), "*.md").Length);
        });

        await suite.Case("a deferred review survives restart with its draft and is resumed when due", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            var goal = new PersonalGoalState(PersonalGoal.Write, Guid.NewGuid(), now, "Tiny stories");
            await SeedAsync(paths, goal);
            var model = new TestModel("defer", "accept");
            DateTimeOffset due;
            await using (var run = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model))
            {
                await StepsAsync(run.Engine, now, 5);
                var deferred = await ReadLifeAsync(paths);
                Check.True(deferred.Active is null && deferred.Journal.Length == 0, "A deferred review was treated as done.");
                var item = deferred.Backlog.Single();
                Check.Equal(goal.Id, item.Goal.Id);
                Check.Equal(3, item.Goal.Project.Scenes.Length);
                Check.True(item.Due > now.AddMinutes(4), "The project wasn't actually deferred.");
                due = item.Due;
            }
            await using var reopened = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model);
            await StepsAsync(reopened.Engine, due, 4);
            Check.Equal(PersonalGoal.Write, (await ReadLifeAsync(paths)).Journal.Single().Goal);
            Check.Equal(1, model.OutlineCalls, "The resumed project discarded its outline.");
        });

        await suite.Case("a web outage defers the same goal and retries without discarding its plan", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            var goal = new PersonalGoalState(PersonalGoal.Explore, Guid.NewGuid(), now, "Cyanotypes");
            await SeedAsync(paths, goal);
            var web = new FakeWeb { FailSearch = true };
            DateTimeOffset due;
            await using (var run = await Fixture.OpenAsync(paths, web))
            {
                await StepsAsync(run.Engine, now, 2);
                var state = await ReadLifeAsync(paths);
                var deferred = state.Backlog.Single();
                Check.Equal(goal.Id, deferred.Goal.Id);
                Check.True(deferred.Goal.Facts.HasFlag(PersonalFact.Planned) && state.Journal.Length == 0, "The failed search lost its plan or got published.");
                due = state.ResearchAfter > deferred.Due ? state.ResearchAfter : deferred.Due;
            }
            web.FailSearch = false;
            await using var reopened = await Fixture.OpenAsync(paths, web);
            await StepsAsync(reopened.Engine, due, 5);
            Check.Equal(FakeWeb.Source, (await ReadLifeAsync(paths)).Journal.Single().Reference);
            Check.Equal(2, web.Queries.Count);
        });

        await suite.Case("a research review can send the same project back for a second source", async root =>
        {
            var paths = new PortablePaths(root);
            var now = DateTimeOffset.UtcNow;
            await SeedAsync(paths, new(PersonalGoal.Explore, Guid.NewGuid(), now, "Cyanotypes"));
            var web = new FakeWeb { ExtraSource = true };
            await using var run = await Fixture.OpenAsync(paths, web, testModel: new("research", "accept"));
            await StepsAsync(run.Engine, now, 5);
            Check.Equal(0, (await ReadLifeAsync(paths)).Journal.Length, "The initial reflection was published despite needing another source.");
            await StepsAsync(run.Engine, now.AddMinutes(5), 4);
            Check.Equal(FakeWeb.Additional, (await ReadLifeAsync(paths)).Journal.Single().Reference);
            Check.Equal("https://example.com/process,https://example.com/additional-process", string.Join(',', web.Reads));
        });

        await suite.Case("a natural diary naming request queues a real content-based rename and preserves colliding files", async root =>
        {
            var paths = new PortablePaths(root);
            const string old = PersonalWriting.Folder + "/4b4590054c3b492d87b7e1c004426cb8.md";
            const string taken = PersonalWriting.Folder + "/The Light on the Glass.md";
            Directory.CreateDirectory(paths.Inside(PersonalWriting.Folder));
            await File.WriteAllTextAsync(paths.Inside(old), "The blue light found the edge of the paper.");
            await File.WriteAllTextAsync(paths.Inside(taken), "My earlier, unrelated story.");
            await new PersonalLife(paths).SaveAsync(new() { Journal = [new(DateTimeOffset.UtcNow, PersonalGoal.Write,
                "Cyanotypes", "An earlier piece.", old)] }, default);
            await using var run = await Fixture.OpenAsync(paths, new FakeWeb());
            await run.Engine.RespondAsync("Не могла бы ты называть записи своего дневника человеческими названиями? Например " +
                Path.GetFileName(old), "user", false, false, default);
            Check.True(run.Output.Lines.Any(line => line.Contains("put the title change on my list")) &&
                File.Exists(paths.Inside(old)), "The acknowledgement didn't reflect the real queued task.");
            await StepsAsync(run.Engine, DateTimeOffset.UtcNow, 2);
            var note = (await ReadLifeAsync(paths)).Journal.Single();
            Check.Equal("The Light on the Glass", note.Title);
            Check.Equal(PersonalWriting.Folder + "/The Light on the Glass (2).md", note.Reference);
            Check.True(!File.Exists(paths.Inside(old)), "The original file name remained.");
            Check.Equal("The blue light found the edge of the paper.", await File.ReadAllTextAsync(paths.Inside(note.Reference)));
            Check.Equal("My earlier, unrelated story.", await File.ReadAllTextAsync(paths.Inside(taken)));
            Check.True(ReplyQuality.ClaimsComputerAction("I'll rename them now so they match the content instead."), "Chat can still promise the unqueued rename.");
        });

        await suite.Case("a personal drawing is reviewed from rendered pixels before a PNG is saved", async root =>
        {
            var now = DateTimeOffset.UtcNow;
            var paths = new PortablePaths(root);
            await SeedAsync(paths, new(PersonalGoal.Sketch, Guid.NewGuid(), now, "Colour and light"));
            var model = new TestModel();
            await using var run = await Fixture.OpenAsync(paths, new FakeWeb(), testModel: model);
            await StepsAsync(run.Engine, now, 3);
            Check.True(model.ReviewedImage && !Directory.Exists(paths.Inside("documents/evelyn-sketches")),
                "The image didn't receive its own review before publication.");
            await Tick(run.Engine, now.AddMinutes(3));
            Check.True(File.Exists(paths.Inside((await ReadLifeAsync(paths)).Journal.Single().Reference)), "The approved PNG wasn't saved.");
        });

        await suite.Case("an A/B choice saves only the selected dialogue response", async root =>
        {
            var paths = new PortablePaths(root);
            await using var run = await Fixture.OpenAsync(paths, new FakeWeb());
            await using var local = new LocalModel(paths, run.Config.Model);
            await using var learning = new DialogueLearning(paths, run.Config, local, run.Output);
            const string prompt = "I love collecting ideas for little stories.";
            await run.Engine.RespondAsync(prompt, "user", false, false, default);
            await learning.MaybeCompareAsync(run.Engine, requested: true, default);
            var file = paths.Inside("data/lora/examples.json");
            Check.True(!File.Exists(file), "The comparison stored an unselected alternative.");
            Check.True(await learning.TryChooseAsync("2", default), "The live selection was not accepted.");
            var saved = JsonSerializer.Deserialize<DialogueExample[]>(await File.ReadAllTextAsync(file), AppConfig.Json)!;
            Check.Equal(new DialogueExample(prompt, TestModel.Alternative), saved.Single());
            Check.True(!await learning.TryChooseAsync("1", default), "A completed choice could be reused.");
        });

        await suite.Case("Google text extraction accepts the matching question and a completed reply", _ =>
        {
            const string question = "How do cyanotypes work?";
            const string page = "Ask anything\nAI Mode reply for How do cyanotypes work?\nLight changes the coating.\nGood response\nShare";
            Check.Equal("Light changes the coating.", GoogleAiMode.ExtractAnswer(page, question, visual: false));
            Check.Equal<string?>(null, GoogleAiMode.ExtractAnswer(page, "A different question", visual: false));
            Check.Equal<string?>(null, GoogleAiMode.ExtractAnswer(page, question));
        });
    }

    private static Task<bool> Tick(CompanionEngine engine, DateTimeOffset now, CancellationToken ct = default, TimeSpan? idle = null) =>
        engine.TickPersonalLifeAsync(now, idle ?? TimeSpan.Zero, quiet: true, canSpeak: false, null,
            _ => throw new InvalidOperationException("Unexpected training."), ct, default);

    private static async Task StepsAsync(CompanionEngine engine, DateTimeOffset start, int steps)
    {
        for (var step = 0; step < steps; step++) await Tick(engine, start.AddMinutes(step));
    }

    private static async Task SeedAsync(PortablePaths paths, PersonalGoalState goal) =>
        await new PersonalLife(paths).SaveAsync(new() { Active = goal }, default);

    private static async Task<PersonalLifeState> ReadLifeAsync(PortablePaths paths)
    {
        var store = new PersonalLife(paths);
        await store.LoadAsync(default);
        return store.State;
    }

    private sealed class FakeWeb : IWebResearch
    {
        internal const string Source = "https://example.com/process";
        internal const string Original = "https://example.com/public-guide";
        internal const string Additional = "https://example.com/additional-process";
        internal List<string> Queries { get; } = [];
        internal List<string> Reads { get; } = [];
        internal bool FailOriginal { get; init; }
        internal bool BlockSearch { get; set; }
        internal bool FailSearch { get; set; }
        internal bool ExtraSource { get; init; }
        internal TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SearchResponse> SearchAsync(string query, bool news, CancellationToken ct)
        {
            Queries.Add(query);
            SearchStarted.TrySetResult();
            if (BlockSearch) await Task.Delay(Timeout.Infinite, ct);
            if (FailSearch) throw new IOException("The public search fixture is temporarily unavailable.");
            SearchHit[] hits = ExtraSource ? [new("Process", Source, ""), new("Another view", Additional, "")] :
                [new("Process", Source, "")];
            return new(query, "fixture", hits, []);
        }

        public Task<WebArticle> ReadAsync(string url, CancellationToken ct)
        {
            Reads.Add(url);
            if (FailOriginal && url == Original && Reads.Count(item => item == Original) == 1)
                throw new IOException("The fixture timed out once.");
            return Task.FromResult(new WebArticle("Process", url,
                string.Concat(Enumerable.Repeat("In a cyanotype, sunlight reacts with the coating and makes a print. ", 6)), false));
        }
    }

    private sealed class TestModel(params string[] decisions) : IChatModel
    {
        internal const string Alternative = "My favourite scraps are the ones that still have a little mystery in them.";
        private readonly Queue<string> reviews = new(decisions);
        internal int OutlineCalls { get; private set; }
        internal bool ReviewedImage { get; private set; }
        internal string OpeningContext { get; private set; } = "";

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, GenerationPurpose purpose, CancellationToken ct)
        {
            if (purpose == GenerationPurpose.PersonalOutline)
            {
                OutlineCalls++;
                return Task.FromResult("""{"title":"The Bell Under Glass","aim":"Ada must give up the last family bell to get a stranger home.","scenes":["Ada finds the broken bell behind the closed ferry office.","She chooses to spend its metal on a new crossing signal.","The ferry answers, and Ada returns to an empty shelf."]}""");
            }
            if (purpose == GenerationPurpose.PersonalReview)
            {
                ReviewedImage |= messages.Any(message => message.Image is { Length: > 100 });
                var decision = reviews.TryDequeue(out var next) ? next : "accept";
                return Task.FromResult(JsonSerializer.Serialize(new { decision, feedback = "Make the price of Ada's choice tangible.", scene = 2 }));
            }
            if (purpose == GenerationPurpose.PersonalScene)
            {
                var instruction = messages[0].Text;
                if (instruction.Contains("scene 1 of")) OpeningContext = messages[^1].Text;
                var seed = instruction.Contains("additional scene") ? "After winter the apprentice returned with a box of salt and whispered that another signal now marked the turning." :
                    instruction.Contains("scene 1 of") ? "Ada unlocked the office, crossed the empty ferry shed and found a cracked bell under the old inventory board." :
                    instruction.Contains("scene 3 of") ? "Before evening the ferry arrived from across the channel and carried the stranger, whose boots left wet tracks on the deck." :
                    instruction.Contains("Rewrite only") ? "When Ada touched the empty hook, the loss changed the price of her choice. She finally put her mother's bell into the forge." :
                    "At the forge Ada bartered her bell for an iron signal, and the smith left a little dent beside the handle.";
                return Task.FromResult(string.Join(' ', Enumerable.Repeat(seed, 7)));
            }
            if (purpose == GenerationPurpose.Sketch)
                return Task.FromResult("""{"title":"A Corner of the Window","background":"#203040","shapes":[{"kind":"rectangle","color":"#9B662B","x":16,"y":16,"w":50,"h":48},{"kind":"ellipse","color":"#F8B722","x":18,"y":30,"w":24,"h":22},{"kind":"triangle","color":"#EBE1B0","x":45,"y":45,"w":21,"h":30}]}""");
            return CompleteAsync(messages, purpose == GenerationPurpose.Metadata, ct);
        }
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            var system = messages[0].Text;
            return Task.FromResult(json ? """{"speak":false,"reply":"","actions":[]}""" :
                system.Contains("second natural English reply") ? Alternative :
                system.Contains("private diary reflection") ? "The cyanotype keeps the shape of the fern. I want to try an uneven edge." :
                system.Contains("existing piece a memorable human title") ? "The Light on the Glass" :
                "I like the kind of thought that keeps one small surprise.");
        }
    }

    private sealed class Output : ICompanionOutput
    {
        internal List<string> Lines { get; } = [];
        public void Write(string text) => Lines.Add(text);
    }

    private sealed record Fixture(CompanionEngine Engine, AppConfig Config, MemoryStore Memory, AudioService Audio,
        WeatherService Weather, Output Output) : IAsyncDisposable
    {
        internal static async Task<Fixture> OpenAsync(PortablePaths paths, FakeWeb web, NeedLevels? initial = null,
            Func<string, CancellationToken, Task<GoogleVisualResult>>? google = null, TestModel? testModel = null)
        {
            var config = new AppConfig();
            config.Audio.Enabled = false;
            config.TurnProcessing.RefineInput = false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile), "You are Evelyn, a desktop companion.");
            var memory = new MemoryStore(paths.Root, config.Memory);
            await memory.InitializeAsync(default);
            var emotions = new EmotionEngine(paths.Root);
            await emotions.InitializeAsync(default);
            if (initial is not null) await emotions.SaveNeedsAsync(initial, default);
            var audio = new AudioService(paths.Root, config.Audio);
            var weather = new WeatherService(config);
            var output = new Output();
            var engine = new CompanionEngine(paths, config, testModel ?? new TestModel(), memory, emotions, new(config.Desktop),
                audio, output, weather, webResearch: web) { PersonalGoogleOverride = google };
            await engine.InitializePersonalLifeAsync(default);
            return new(engine, config, memory, audio, weather, output);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Audio.DisposeAsync();
            Weather.Dispose();
        }
    }
}
