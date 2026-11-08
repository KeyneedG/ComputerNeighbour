using System.Buffers.Binary;
using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Personality;

namespace ComputerNeighbour.Tests;

internal static class SpeechTests
{
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("speech renders in parallel and plays in reply order", async _ =>
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var releaseFirst = Signal();
            var secondReady = Signal();
            var heard = new List<int>();
            var disposed = 0;
            var playback = SpeechQueue.PlayAsync(2, 2, () => new Lane(() => Interlocked.Increment(ref disposed)),
                async (_, index, ct) =>
                {
                    if (index == 0) await releaseFirst.Task.WaitAsync(ct);
                    else secondReady.SetResult();
                    return new([(byte)index, 0], 24000, 1);
                }, (audio, _) => { heard.Add(audio.Samples[0]); return Task.CompletedTask; }, budget.Token);
            await secondReady.Task.WaitAsync(budget.Token);
            Check.Equal(0, heard.Count, "A later sentence played before the first was ready.");
            releaseFirst.SetResult();
            Check.Equal<Exception?>(null, await playback);
            Check.Equal("0,1", string.Join(',', heard));
            Check.Equal(2, disposed);
        });

        await suite.Case("one speech worker prefetches during playback and stops on interruption", async _ =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var renderingNext = Signal();
            var disposed = false;
            var playback = SpeechQueue.PlayAsync(2, 1, () => new Lane(() => disposed = true),
                async (_, index, ct) =>
                {
                    if (index == 1)
                    {
                        renderingNext.SetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    return new([0, 0], 24000, 1);
                }, async (_, ct) =>
                {
                    await renderingNext.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                    stop.Cancel();
                    ct.ThrowIfCancellationRequested();
                }, stop.Token);
            await Check.ThrowsAsync<OperationCanceledException>(() => playback);
            Check.True(disposed, "Cancellation left the prefetch worker running.");
        });

        await suite.Case("speech detects a loud cutoff and keeps a natural ending", _ =>
        {
            var samples = new byte[24000 * 2 / 5];
            for (var i = 0; i < samples.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i), 3000);
            var audio = new NativeAudio.PcmAudio(samples, 24000, 1);
            Check.True(audio.HasAbruptEnding(), "A sustained signal cut at the final sample was accepted.");
            Array.Clear(samples, samples.Length - 1200, 1200);
            Check.True(!audio.HasAbruptEnding(), "A quiet ending was rejected.");
            Check.Equal("Are you okay?\"", SpeechDelivery.WithSentenceEnding("Are you okay?\""));
            Check.Equal("Check the permissions.", SpeechDelivery.WithSentenceEnding("Check the permissions"));
        });

        await suite.Case("speech labels use the latest user turn and actual sentence boundaries", async _ =>
        {
            const string reply = "Dr. Lane is here to help\nif you want to talk. I'm here, too.";
            var now = DateTimeOffset.Now;
            var model = new ContextModel();
            var plan = await SpeechDirector.PlanAsync(reply, reply,
                [new(now, "assistant", "Great news!"), new(now, "user", "I'm having a rough day."), new(now, "assistant", reply)],
                "bright and playful", model, default);
            Check.Equal("I'm having a rough day.", model.LatestUser);
            Check.Equal(2, plan.Count);
            Check.Equal("Dr. Lane is here to help\nif you want to talk.", plan[0].Text);
            Check.True(plan.All(sentence => sentence.Emotion == SpeechEmotion.Warm), "Directions shifted across a soft line break.");
        });
        await suite.Case("a mixed reply can shift from pride to nostalgia between spoken sentences", async _ =>
        {
            const string reply = "I'm proud we made it through. I'll miss those late edits.";
            var plan = await SpeechDirector.PlanAsync(reply, reply, [], "thoughtful",
                new ContextModel("proud", "nostalgic"), default);
            Check.Equal(SpeechEmotion.Proud, plan[0].Emotion);
            Check.Equal(SpeechEmotion.Nostalgic, plan[1].Emotion);
            Check.True(RecentReaction.Labels.All(label => SpeechDelivery.TryReadEmotion(label, out var _)), "A reaction has no voice direction.");
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lane(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ContextModel(params string[] labels) : ITurnContextModel
    {
        public string? LatestUser { get; private set; }
        public Task<string> ClassifyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PlanSpeechAsync(IReadOnlyList<ChatMessage> messages, int sentenceCount, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(messages[^1].Text);
            LatestUser = document.RootElement.GetProperty("latestUser").GetString();
            Check.Equal(2, sentenceCount);
            return Task.FromResult<string?>(JsonSerializer.Serialize(new { emotions = labels.Length == 0 ? ["warm","warm"] : labels }));
        }
    }
}
