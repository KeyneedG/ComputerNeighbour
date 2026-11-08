using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Audio;

// A lane owns one speech.cpp worker and sends it one synthesis request at a time.
internal sealed class QwenVoiceWorker(ProcessStartInfo start, int timeoutSeconds) : IAsyncDisposable
{
    private const int MaxPcmBytes = 32_000_000;
    private Process? process;
    private Task<string>? errors;
    private int sampleRate;
    private int nextRequest;

    public async Task<NativeAudio.PcmAudio> SynthesizeAsync(SpeechSentence sentence, bool retry, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 1800)));
        var ct = budget.Token;
        if (process is null)
        {
            ct.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new IOException("Could not start speech.cpp.");
            ChildProcessJob.Attach(process);
            errors = AudioService.DrainBoundedAsync(process.StandardError, CancellationToken.None);
            using var ready = await ReadAsync(ct);
            var message = ready.RootElement;
            var kind = message.GetProperty("type").GetString();
            if (kind == "fatal") throw Failure(message);
            if (kind != "ready" || message.GetProperty("protocol").GetInt32() != 4)
                throw new InvalidDataException("Unexpected speech.cpp worker protocol.");
            sampleRate = message.GetProperty("model").GetProperty("sample_rate").GetInt32();
            if (sampleRate is < 8000 or > 192000) throw new InvalidDataException("Invalid speech sample rate.");
        }

        var id = (++nextRequest).ToString(CultureInfo.InvariantCulture);
        var instruction = SpeechDelivery.Instruction(sentence.Emotion);
        if (retry) instruction += " Articulate the final syllable fully and pause at the end.";
        var request = JsonSerializer.Serialize(new
        {
            type = "synthesize", id, text = sentence.Text, instructions = instruction, temperature = retry ? 0.55 : 0.72
        });
        await process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);

        using var pcm = new MemoryStream();
        var sequence = 0;
        while (true)
        {
            using var line = await ReadAsync(ct);
            var message = line.RootElement;
            var kind = message.GetProperty("type").GetString();
            if (kind is "error" or "fatal") throw Failure(message);
            if (message.GetProperty("id").GetString() != id) throw new InvalidDataException("Unexpected speech request ID.");
            switch (kind)
            {
                case "progress": break;
                case "chunk":
                    var chunk = message.GetProperty("pcm").GetBytesFromBase64();
                    if (message.GetProperty("seq").GetInt32() != sequence++ || chunk.Length % 2 != 0 || pcm.Length + chunk.Length > MaxPcmBytes)
                        throw new InvalidDataException("Invalid speech PCM chunk.");
                    pcm.Write(chunk);
                    break;
                case "end":
                    if (message.GetProperty("stop").GetString() != "complete" || pcm.Length == 0 || message.GetProperty("samples").GetInt64() != pcm.Length / 2)
                        throw new InvalidDataException("Speech synthesis ended with incomplete audio.");
                    return new(pcm.ToArray(), sampleRate, 1);
                default: throw new InvalidDataException("Unexpected speech.cpp worker response.");
            }
        }
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken ct)
    {
        if (await process!.StandardOutput.ReadLineAsync(ct) is { } line) return JsonDocument.Parse(line);
        var detail = errors is { IsCompletedSuccessfully: true } ? errors.Result : "";
        throw new IOException("speech.cpp stopped before completing synthesis. " + detail.Trim());
    }

    private static IOException Failure(JsonElement message) =>
        new("speech.cpp: " + message.GetProperty("error").GetProperty("message").GetString());

    public async ValueTask DisposeAsync()
    {
        if (process is null) return;
        using (process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync();
            if (errors is not null) await errors;
        }
    }
}
