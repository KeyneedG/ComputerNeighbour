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
            Check.Equal("AskGoogle,Reflect", string.Join(',', google));
            var fallback = AutonomyPlanner.Plan(PersonalGoal.Company, PersonalFact.FoundSource, lonely,
                action => action != PersonalAction.AskGoogle);
            Check.Equal("ReadArticle,Reflect", string.Join(',', fallback));
        });

        await suite.Case("an interrupted personal lookup preserves its goal across restart and keeps its diary separate", async root =>
        {
            var now = DateTimeOffset.UtcNow;
            var paths = new PortablePaths(root);
            var goal = new PersonalGoalState(PersonalGoal.Explore, Guid.NewGuid(), now, "Cyanotypes");
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
            await Tick(reopened.Engine, now.AddMinutes(1));
            await Tick(reopened.Engine, now.AddMinutes(2));
            await Tick(reopened.Engine, now.AddMinutes(3));
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
            await Tick(reopened.Engine, now.AddMinutes(1));
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
            await SeedAsync(paths, new(PersonalGoal.Company, Guid.NewGuid(), now, "Cyanotypes"));
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
            await Tick(run.Engine, now.AddMinutes(1));
            await Tick(run.Engine, now.AddMinutes(2));
            await Tick(run.Engine, now.AddMinutes(3));
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
            Check.Equal(FakeWeb.Original, store.State.Challenge?.Target);
            var now = DateTimeOffset.UtcNow;
            // A fresh session selects the recorded learning challenge from its checkpoint.
            await store.SaveAsync(store.State with
                { Active = new(PersonalGoal.Recover, Guid.NewGuid(), now, "Reading public web sources") }, default);
            await using var reopened = await Fixture.OpenAsync(paths, web);
            for (var step = 0; step < 4; step++) await Tick(reopened.Engine, now.AddMinutes(step));
            var done = await ReadLifeAsync(paths);
            Check.True(done.Challenge is null && done.Notice?.Text.Contains("managed to read") == true, "No verified recovery was recorded.");
            Check.Equal(2, web.Reads.Count(url => url == FakeWeb.Original));
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
        internal List<string> Queries { get; } = [];
        internal List<string> Reads { get; } = [];
        internal bool FailOriginal { get; init; }
        internal bool BlockSearch { get; set; }
        internal TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SearchResponse> SearchAsync(string query, bool news, CancellationToken ct)
        {
            Queries.Add(query);
            SearchStarted.TrySetResult();
            if (BlockSearch) await Task.Delay(Timeout.Infinite, ct);
            return new(query, "fixture", [new("Process", Source, "")], []);
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

    private sealed class TestModel : IChatModel
    {
        internal const string Alternative = "My favourite scraps are the ones that still have a little mystery in them.";
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            var system = messages[0].Text;
            return Task.FromResult(json ? """{"speak":false,"reply":"","actions":[]}""" :
                system.Contains("second natural English reply") ? Alternative :
                system.Contains("private diary entry") ? "The cyanotype keeps the shape of the fern. I want to try an uneven edge." :
                system.Contains("vignette or poem") ? "A loose page waited in the blue light." :
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
            Func<string, CancellationToken, Task<GoogleVisualResult>>? google = null)
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
            var engine = new CompanionEngine(paths, config, new TestModel(), memory, emotions, new(config.Desktop),
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
