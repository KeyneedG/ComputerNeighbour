namespace ComputerNeighbour.Audio;

internal static class SpeechQueue
{
    // Synthesis lanes claim work independently. The player consumes results in reply order.
    internal static async Task<Exception?> PlayAsync<TWorker>(int count, int lanes, Func<TWorker> createWorker,
        Func<TWorker, int, CancellationToken, Task<NativeAudio.PcmAudio>> render,
        Func<NativeAudio.PcmAudio, CancellationToken, Task> play, CancellationToken token) where TWorker : IAsyncDisposable
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ready = Enumerable.Range(0, count).Select(_ =>
            new TaskCompletionSource<(NativeAudio.PcmAudio? Audio, Exception? Failure)>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var claimed = -1;

        async Task RenderLaneAsync()
        {
            var index = Interlocked.Increment(ref claimed);
            try
            {
                await using var worker = createWorker();
                for (; index < count && !stopping.IsCancellationRequested; index = Interlocked.Increment(ref claimed))
                    ready[index].SetResult((await render(worker, index, stopping.Token), null));
            }
            catch (Exception ex) when (index < count)
            {
                ready[index].TrySetResult((null, ex));
            }
        }

        var workers = Enumerable.Range(0, Math.Min(count, lanes)).Select(_ => RenderLaneAsync()).ToArray();
        try
        {
            for (var i = 0; i < ready.Length; i++)
            {
                var result = await ready[i].Task.WaitAsync(token);
                if (result.Failure is { } failure) return failure;
                await play(result.Audio!, token);
            }
            return null;
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(workers);
        }
    }
}
