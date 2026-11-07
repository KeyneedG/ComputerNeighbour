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

internal static class RelationshipEngineTests
{
    private static string Decision(string attitude, string impression, EmotionDelta? emotion = null, List<ToolRequest>? actions = null, bool speak = true) =>
        JsonSerializer.Serialize(new CompanionDecision
        {
            Speak = speak, Reply = speak ? "I appreciate the conversation." : "", Memory = "",
            Emotion = emotion ?? new EmotionDelta(),
            Relationship = new RelationshipUpdate { CurrentAttitude = attitude, RecentImpression = impression, Appreciates = ["Thoughtfulness"], Concerns = [], Boundaries = ["Respect requests for quiet"] },
            Actions = actions ?? []
        }, AppConfig.Json);

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("main-user dialogue persists separate relationship narrative and emotional scores and survives forgetting dialogue", async root =>
        {
            var model = new ScriptedModel("I appreciate how carefully you listened.",Decision("Comfortable with Alex's thoughtful style.", "Alex took time to listen.", new() { Warmth = 0.03, Trust = 0.02, Closeness = 0.02 }));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("Thank you for taking the time to explain that.", "user", false, false, CancellationToken.None);
            var relationship = await OpenRelationshipAsync(root);
            Check.Equal("Comfortable with Alex's thoughtful style.", relationship.State.CurrentAttitude);
            Check.Equal("Alex took time to listen.", relationship.State.RecentImpression);
            Check.Equal("Alex", relationship.State.UserName);
            Check.Equal(1L, relationship.State.Interactions);
            Check.Equal(fixture.Emotions.State.Warmth, relationship.State.Warmth);
            Check.Equal(fixture.Emotions.State.Trust, relationship.State.Trust);
            Check.Equal(fixture.Emotions.State.Closeness, relationship.State.Closeness);
            Check.True(relationship.State.Trust > 0.35, "The trusted main-user interaction did not update the relationship score.");
            Check.Equal(2,model.Calls.Count);
            Check.True(model.Calls.Any(call=>call.Any(m=>m.Text.Contains("RELATIONSHIP",StringComparison.OrdinalIgnoreCase))), "The model did not receive separate relationship context during state maintenance.");
            await fixture.Memory.ForgetAsync();
            Check.Equal(0, fixture.Memory.Recent.Count);
            var afterForget = await OpenRelationshipAsync(root);
            Check.Equal(relationship.State.CurrentAttitude, afterForget.State.CurrentAttitude);
            Check.Equal(relationship.State.Trust, afterForget.State.Trust);
            Check.Equal(1L, afterForget.State.Interactions);
            Check.True((await fixture.Engine.GetRelationshipAsync(CancellationToken.None)).Contains("Alex took time to listen."), "Forgetting dialogue erased the separate relationship record.");
        });

        await suite.Case("unknown and other speakers cannot overwrite the main user's relationship or change warmth trust and closeness", async root =>
        {
            var model = new ScriptedModel(
                "I enjoyed that exchange too, Alex.",
                Decision("Warm toward Alex while learning their preferences.", "Alex was considerate.", new() { Warmth = 0.02, Trust = 0.02, Closeness = 0.01 }),
                "That sounded hostile, but I will keep the speakers separate.",
                Decision("Unrelated stranger's hostility must not replace this.", "A stranger was rude.", new() { Warmth = -0.03, Trust = -0.03, Closeness = -0.03, Stress = 0.02 }),
                "I can hear Alice's request without attributing it to Alex.",
                Decision("Another person's demands must not replace this.", "Alice demanded attention.", new() { Warmth = -0.03, Trust = -0.03, Closeness = -0.03, Stress = 0.02 }));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("I enjoyed talking with you.", "user", false, false, CancellationToken.None);
            var baseline = (await OpenRelationshipAsync(root)).State;
            var baselineStress = fixture.Emotions.State.Stress;
            await fixture.Engine.RespondAsync("A different voice says something rude.", "unidentified-test", false, false, CancellationToken.None);
            await fixture.Engine.RespondAsync("A separately enrolled person demands attention.", "alice", false, false, CancellationToken.None);
            var after = (await OpenRelationshipAsync(root)).State;
            Check.Equal(baseline.CurrentAttitude, after.CurrentAttitude);
            Check.Equal(baseline.RecentImpression, after.RecentImpression);
            Check.Equal(baseline.Interactions, after.Interactions);
            Check.Equal(baseline.Warmth, after.Warmth); Check.Equal(baseline.Trust, after.Trust); Check.Equal(baseline.Closeness, after.Closeness);
            Check.Equal(baseline.Warmth, fixture.Emotions.State.Warmth);
            Check.Equal(baseline.Trust, fixture.Emotions.State.Trust);
            Check.Equal(baseline.Closeness, fixture.Emotions.State.Closeness);
            Check.True(fixture.Emotions.State.Stress > baselineStress, "Other speakers should still be able to affect transient mood without changing the user's relationship.");
            Check.Equal(6, model.Calls.Count);
        });

        await suite.Case("an autonomous file proposal does not revise the main-user relationship or trust", async root =>
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { path = "documents/idea.md", content = "A useful note proposed during a quiet moment." }))!;
            var model = new ScriptedModel(
                "I will leave room for quiet, Alex.",
                Decision("A stable, friendly attitude toward Alex.", "Alex asked for a calm workspace.", new() { Trust = 0.02 }),
                Decision("This autonomous guess must not become the user's relationship.", "The character had an idea while alone.", new() { Warmth = 0.03, Trust = 0.03, Closeness = 0.03 }, [new("create_file", args)], speak: false));
            await using var fixture = await Fixture.CreateAsync(root, model);
            fixture.Config.Permissions.AllowAutonomousFileActions = true;
            fixture.Config.Permissions.WriteFiles = true;
            fixture.Config.Permissions.FullFileAccess = false;
            fixture.Config.Permissions.ConfirmFileChanges = true;
            fixture.Config.Permissions.ConfirmActions = true;
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await fixture.Engine.RespondAsync("I appreciate a calm workspace.", "user", false, false, CancellationToken.None);
            var baseline = (await OpenRelationshipAsync(root)).State;
            await fixture.Engine.RespondAsync("Scheduled opportunity to consider a useful note.", "environment", true, false, CancellationToken.None);
            var after = (await OpenRelationshipAsync(root)).State;
            Check.Equal(baseline.CurrentAttitude, after.CurrentAttitude);
            Check.Equal(baseline.RecentImpression, after.RecentImpression);
            Check.Equal(baseline.Interactions, after.Interactions);
            Check.Equal(baseline.Trust, after.Trust);
            Check.Equal(baseline.Trust, fixture.Emotions.State.Trust);
            Check.True(fixture.Output.Lines.Any(s => s.Contains("[Approval") && s.Contains("character initiative")), "The autonomous file action was not staged as a reviewable proposal.");
            Check.True(!File.Exists(Path.Combine(root, "documents", "idea.md")), "The pending autonomous file proposal wrote the target before approval.");
            Check.Equal(3, model.Calls.Count);
        });
    }

    private static async Task<RelationshipStore> OpenRelationshipAsync(string root)
    {
        var store = new RelationshipStore(root, "Alex");
        await store.InitializeAsync();
        return store;
    }

    private sealed class ScriptedModel(params string[] responses) : IChatModel
    {
        public List<ChatMessage[]> Calls { get; } = [];
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(messages.ToArray());
            if (Calls.Count > responses.Length) throw new InvalidOperationException("Unexpected model call in a relationship integration test.");
            return Task.FromResult(responses[Calls.Count - 1]);
        }
    }
    private sealed class CapturingOutput : ICompanionOutput
    {
        public List<string> Lines { get; } = [];
        public void Write(string text) => Lines.Add(text);
    }
    private sealed class UnusedBrowser : IBrowserReader
    {
        public Task<BrowserPage> ReadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Relationship tests must not read a real browser.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required AppConfig Config { get; init; }
        public required MemoryStore Memory { get; init; }
        public required EmotionEngine Emotions { get; init; }
        public required CompanionEngine Engine { get; init; }
        public required AudioService Audio { get; init; }
        public required WeatherService Weather { get; init; }
        public required CapturingOutput Output { get; init; }
        public static async Task<Fixture> CreateAsync(string root, IChatModel model)
        {
            var paths = new PortablePaths(root);
            var config = new AppConfig { UserName = "Alex" };
            config.Audio.Enabled = false;
            config.Permissions.Microphone = false;
            config.Permissions.ScreenCapture = false;
            config.Permissions.ForegroundWindow = false;
            config.Permissions.SystemStatus = false;
            config.Permissions.WeatherNetwork = false;
            config.Permissions.ReadBrowser = false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile), "You are a calm fictional neighbour. Reply in English.");
            var memory = new MemoryStore(root, config.Memory); await memory.InitializeAsync();
            var emotions = new EmotionEngine(root); await emotions.InitializeAsync();
            var audio = new AudioService(root, config.Audio);
            var weather = new WeatherService(config);
            var output = new CapturingOutput();
            return new Fixture
            {
                Config = config, Memory = memory, Emotions = emotions, Audio = audio, Weather = weather, Output = output,
                Engine = new CompanionEngine(paths, config, model, memory, emotions, new WindowsDesktop(config.Desktop), audio, output, weather, new UnusedBrowser()) { Paused = true }
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
