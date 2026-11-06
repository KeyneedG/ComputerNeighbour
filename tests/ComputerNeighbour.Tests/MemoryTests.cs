using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;

namespace ComputerNeighbour.Tests;

internal static class MemoryTests
{
    private static readonly MemoryOptions Small = new() { MaxRecentCharacters = 1000, MaxSummaryCharacters = 500, MaxJournalBytes = 4096 };
    private static async Task<MemoryStore> OpenAsync(string root, MemoryOptions? options = null)
    {
        var store = new MemoryStore(root, options ?? Small);
        await store.InitializeAsync();
        return store;
    }

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("memory reload retains dialogue, timestamps and distinct speakers", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "I prefer tea.", "alex");
            await store.AppendAsync("user", "Я предпочитаю кофе.", "maria");
            var restored = await OpenAsync(root);
            Check.Equal(2, restored.Recent.Count);
            Check.Equal("alex", restored.Recent[0].SpeakerId);
            Check.Equal("maria", restored.Recent[1].SpeakerId);
            Check.Equal("Я предпочитаю кофе.", restored.Recent[1].Text);
            Check.True(restored.Recent[0].At <= DateTimeOffset.UtcNow, "Timestamp should describe an observed past event.");
        });
        await suite.Case("compaction persists summary and preserves speaker attribution in its input", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "I prefer tea.", "alex");
            await store.AppendAsync("user", "Coffee, please.", "unknown");
            await store.CompactAsync((input, _) =>
            {
                Check.True(input.Contains("speaker=alex") && input.Contains("speaker=unknown"), "Summary input lost speaker identity.");
                return Task.FromResult("Alex prefers tea. An unidentified speaker requested coffee.");
            });
            var restored = await OpenAsync(root);
            Check.Equal("Alex prefers tea. An unidentified speaker requested coffee.", restored.Summary);
            Check.Equal(0, restored.Recent.Count);
        });
        await suite.Case("failed and empty model summaries leave original memory intact", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "Please remember our meeting on Friday.");
            await Check.ThrowsAsync<IOException>(() => store.CompactAsync((_, _) => throw new IOException("model offline")));
            await Check.ThrowsAsync<InvalidDataException>(() => store.CompactAsync((_, _) => Task.FromResult(" \n")));
            Check.Equal("Please remember our meeting on Friday.", (await OpenAsync(root)).Recent.Single().Text);
            Check.Equal("", store.Summary);
        });
        await suite.Case("dialogue appended while a model summarizes survives compaction", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "Earlier fact.", "alex");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compact = store.CompactAsync(async (_, _) => { entered.SetResult(); await release.Task; return "Earlier fact retained."; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.AppendAsync("user", "A new fact while you worked.", "maria");
            release.SetResult();
            await compact;
            Check.Equal("A new fact while you worked.", store.Recent.Single().Text);
            Check.Equal("maria", store.Recent.Single().SpeakerId);
            Check.Equal("Earlier fact retained.", (await OpenAsync(root)).Summary);
        });
        await suite.Case("forget invalidates an in-flight summary and removes recoverable history", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "A fact that must be forgotten.");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compact = store.CompactAsync(async (_, _) => { entered.SetResult(); await release.Task; return "Stale sensitive fact."; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.ForgetAsync();
            release.SetResult();
            await compact;
            var restored = await OpenAsync(root);
            Check.Equal("", restored.Summary);
            Check.Equal(0, restored.Recent.Count);
            Check.True(!Directory.EnumerateFiles(Path.Combine(root, "data", "memory")).Any(p => p.EndsWith(".bak") || p.Contains("journal")), "Forgotten history remains in backups or audit journals.");
        });
        await suite.Case("cancelled summary leaves committed memory intact", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "Keep this until compaction actually succeeds.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => store.CompactAsync((_, _) => Task.FromResult("replacement"), cancelled.Token));
            Check.Equal(1, (await OpenAsync(root)).Recent.Count);
        });
        await suite.Case("recent context and generated summary respect configured limits", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", new string('a', 700));
            await store.AppendAsync("assistant", new string('b', 700));
            Check.True(store.NeedsCompaction, "Configured memory threshold should trigger compaction.");
            Check.True(store.Recent.Sum(e => e.Text.Length) <= Small.MaxRecentCharacters, "Recent context exceeded its character budget.");
            await store.CompactAsync((_, _) => Task.FromResult(new string('s', 800)));
            Check.True(store.Summary.Length <= Small.MaxSummaryCharacters, "Long-term summary exceeded its character budget.");
        });
        await suite.Case("bounded journals rotate without dropping authoritative recent dialogue", async root =>
        {
            var store = await OpenAsync(root);
            for (var i = 0; i < 12; i++) await store.AppendAsync("user", i + ":" + new string('x', 600));
            var journals = Directory.GetFiles(Path.Combine(root, "data", "memory"), "journal*");
            Check.Equal(2, journals.Length);
            Check.True(journals.All(p => new FileInfo(p).Length <= Small.MaxJournalBytes), "Journal rotation exceeded its byte limit.");
            Check.True(store.StoredCharacters > 7000, "Journal rotation discarded uncompressed authoritative dialogue.");
        });
        await suite.Case("damaged snapshots recover the last valid backup and preserve corruption evidence", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "First durable fact.");
            await store.AppendAsync("user", "Newest event.");
            var statePath = Path.Combine(root, "data", "memory", "state.json");
            await File.WriteAllTextAsync(statePath, "{ damaged JSON");
            var restored = await OpenAsync(root);
            Check.True(restored.RecoveryNotice is not null, "Recovery should be reported.");
            Check.Equal("First durable fact.", restored.Recent.Single().Text);
            Check.True(File.Exists(statePath + ".corrupt"), "Damaged state should remain available for recovery.");
        });
        await suite.Case("missing snapshot with an existing journal fails instead of silently forgetting", async root =>
        {
            var store = await OpenAsync(root);
            await store.AppendAsync("user", "Existing memory.");
            File.Delete(Path.Combine(root, "data", "memory", "state.json"));
            File.Delete(Path.Combine(root, "data", "memory", "state.json.bak"));
            await Check.ThrowsAsync<InvalidDataException>(() => OpenAsync(root));
        });
        await suite.Case("private reasoning roles are rejected before persistence", async root =>
        {
            var store = await OpenAsync(root);
            await Check.ThrowsAsync<ArgumentException>(() => store.AppendAsync("analysis", "private chain of thought"));
            await Check.ThrowsAsync<ArgumentException>(() => store.AppendAsync("REASONING", "private chain of thought"));
            Check.Equal(0, store.Recent.Count);
        });
        await suite.Case("personality clamps untrusted deltas, ignores nonfinite changes and persists relationship", async root =>
        {
            var engine = new EmotionEngine(root);
            await engine.InitializeAsync();
            var initial = engine.State;
            await engine.ApplyAsync(new EmotionDelta { Trust = 999, Warmth = -999, Closeness = 999, Valence = -999, Energy = double.PositiveInfinity, Stress = double.NaN, Reason = "An actual conversation event." });
            Check.True(engine.State.Trust - initial.Trust <= 0.025001, "Trust changed implausibly in a single turn.");
            Check.True(initial.Warmth - engine.State.Warmth <= 0.040001, "Warmth changed implausibly in a single turn.");
            Check.True(engine.State.Closeness - initial.Closeness <= 0.025001, "Closeness changed implausibly in a single turn.");
            Check.True(double.IsFinite(engine.State.Energy) && double.IsFinite(engine.State.Stress), "Untrusted nonfinite changes corrupted mood.");
            for (var i = 0; i < 30; i++) await engine.ApplyAsync(new EmotionDelta { Trust = 100, Stress = -100, Valence = -100 });
            Check.True(engine.State.Trust <= 1 && engine.State.Stress >= 0 && engine.State.Valence >= -1, "State escaped its bounds.");
            var restored = new EmotionEngine(root);
            await restored.InitializeAsync();
            Check.Equal(engine.State.Trust, restored.State.Trust);
            Check.Equal(31L, restored.State.Interactions);
            Check.True(restored.DescribeContext(DateTimeOffset.Now, null, TimeSpan.FromMinutes(15)).Contains("Weather is unavailable"), "Context should not invent weather.");
        });
    }
}
