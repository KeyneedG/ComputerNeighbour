using ComputerNeighbour.Personality;

namespace ComputerNeighbour.Tests;

internal static class RelationshipTests
{
    private static async Task<RelationshipStore> OpenAsync(string root, string userName = "Alex")
    {
        var store = new RelationshipStore(root, userName);
        await store.InitializeAsync();
        return store;
    }

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("separate relationship state persists narrative and readable projection independently from conversation memory", async root =>
        {
            var store = await OpenAsync(root);
            Check.Equal(0L, store.State.Interactions);
            Check.True(!store.State.CurrentAttitude.Contains("shared memories"), "Initial relationship invented shared memories.");
            await store.UpdateAsync(new()
            {
                CurrentAttitude = "Growing comfortable with Alex's thoughtful style.",
                RecentImpression = "Alex listened patiently during a difficult explanation.",
                Appreciates = ["Patience", "Clear questions"], Concerns = ["No confirmed concern"], Boundaries = ["Respect requests for silence"]
            }, new EmotionState { Warmth = 0.72, Trust = 0.55, Closeness = 0.31 });
            var restored = await OpenAsync(root);
            Check.Equal("Alex", restored.State.UserName);
            Check.Equal("Growing comfortable with Alex's thoughtful style.", restored.State.CurrentAttitude);
            Check.Equal("Alex listened patiently during a difficult explanation.", restored.State.RecentImpression);
            Check.Equal(0.72, restored.State.Warmth); Check.Equal(0.55, restored.State.Trust); Check.Equal(0.31, restored.State.Closeness);
            Check.Equal(1L, restored.State.Interactions);
            Check.True(restored.State.Appreciates.SequenceEqual(new[] { "Patience", "Clear questions" }), "Relationship factors were not persisted.");
            var markdown = await File.ReadAllTextAsync(Path.Combine(root, "data", "personality", "relationship.md"));
            Check.True(markdown.Contains("Growing comfortable") && markdown.Contains("Respect requests for silence"), "Readable relationship projection is incomplete.");
            Check.True(!Directory.Exists(Path.Combine(root, "data", "memory")), "Separate relationship state wrote into dialogue memory.");
            Check.True(restored.DescribeContext().Contains("main-user updates=1"), "Model context omitted relationship update count.");
        });
        await suite.Case("null relationship updates preserve narrative while synchronizing valid emotional scores", async root =>
        {
            var store = await OpenAsync(root);
            await store.UpdateAsync(new() { CurrentAttitude = "Friendly but still learning the user's preferences.", Appreciates = ["Honesty"] }, new EmotionState());
            await store.UpdateAsync(null, new EmotionState { Warmth = 0.8, Trust = 0.6, Closeness = 0.4 });
            Check.Equal("Friendly but still learning the user's preferences.", store.State.CurrentAttitude);
            Check.Equal("Honesty", store.State.Appreciates.Single());
            Check.Equal(0.8, store.State.Warmth); Check.Equal(0.6, store.State.Trust); Check.Equal(0.4, store.State.Closeness);
            Check.Equal(2L, store.State.Interactions);
            Check.Equal(0.6, (await OpenAsync(root)).State.Trust);
        });
        await suite.Case("relationship input is bounded, deduplicated and cannot inject terminal control characters", async root =>
        {
            var store = await OpenAsync(root);
            await store.UpdateAsync(new()
            {
                CurrentAttitude = new string('a', 3000), RecentImpression = new string('b', 2000),
                Appreciates = ["  Patience\n", "patience", "\u001b[2J", .. Enumerable.Range(0, 20).Select(i => i + ":" + new string('c', 500))],
                Concerns = [], Boundaries = ["  Be\tclear\nabout uncertainty.  "]
            }, new EmotionState { Warmth = 99, Trust = -99, Closeness = double.NaN });
            Check.Equal(1200, store.State.CurrentAttitude.Length);
            Check.Equal(600, store.State.RecentImpression.Length);
            Check.Equal(10, store.State.Appreciates.Count);
            Check.True(store.State.Appreciates.All(v => v.Length <= 240 && !v.Any(char.IsControl)), "An unbounded/control-character list item survived.");
            Check.Equal(1.0, store.State.Warmth); Check.Equal(0.0, store.State.Trust); Check.Equal(0.2, store.State.Closeness);
            Check.Equal("Be clear about uncertainty.", store.State.Boundaries.Single());
            Check.Equal(1.0, (await OpenAsync(root)).State.Warmth);
        });
        await suite.Case("damaged relationship JSON restores its valid backup and preserves corruption evidence", async root =>
        {
            var store = await OpenAsync(root);
            await store.UpdateAsync(new() { CurrentAttitude = "First saved attitude." }, new EmotionState());
            await store.UpdateAsync(new() { CurrentAttitude = "Newest attitude." }, new EmotionState());
            var file = Path.Combine(root, "data", "personality", "relationship.json");
            await File.WriteAllTextAsync(file, "{ damaged state");
            var recovered = await OpenAsync(root);
            Check.Equal("First saved attitude.", recovered.State.CurrentAttitude);
            Check.True(recovered.RecoveryNotice is not null, "Relationship recovery was not reported.");
            Check.True(File.Exists(file + ".corrupt"), "Damaged relationship evidence was lost.");
            Check.Equal("First saved attitude.", (await OpenAsync(root)).State.CurrentAttitude);
        });
        await suite.Case("cancelled relationship update leaves committed narrative unchanged and missing-primary recovery works", async root =>
        {
            var store = await OpenAsync(root);
            await store.UpdateAsync(new() { CurrentAttitude = "Keep this attitude." }, new EmotionState());
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => store.UpdateAsync(new() { CurrentAttitude = "Must not save." }, new EmotionState(), cancellation.Token));
            Check.Equal("Keep this attitude.", (await OpenAsync(root)).State.CurrentAttitude);
            var file = Path.Combine(root, "data", "personality", "relationship.json");
            File.Move(file, file + ".moved-for-test");
            var recovered = await OpenAsync(root);
            Check.True(recovered.RecoveryNotice is not null, "Missing-primary relationship recovery was not reported.");
            Check.Equal(0L, recovered.State.Interactions); // The backup predates the first interaction.
        });
    }
}
