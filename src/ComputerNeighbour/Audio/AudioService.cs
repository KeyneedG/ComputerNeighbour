using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Audio;

public sealed class AudioOptions
{
    public bool Enabled { get; set; } = true;
    public int PushToTalkVirtualKey { get; set; } = 0x79;
    public int InputDeviceId { get; set; } = -1;
    public int OutputDeviceId { get; set; } = -1;
    public int SampleRate { get; set; } = 16000;
    public int MaxRecordingSeconds { get; set; } = 30;
    public string WhisperExecutable { get; set; } = "runtimes/whisper/whisper-cli.exe";
    public string WhisperModel { get; set; } = "models/whisper/ggml-base.bin";
    public string PiperExecutable { get; set; } = "runtimes/piper/piper.exe";
    public string PiperModel { get; set; } = "models/piper/en_US-lessac-medium.onnx";
    public string TtsEngine { get; set; } = "piper";
    public string QwenTtsExecutable { get; set; } = "runtimes/speech/speech.exe";
    public string QwenTtsModel { get; set; } = "models/qwen-tts/Qwen3-TTS-12Hz-1.7B-CustomVoice-Q5_K.gguf";
    public string QwenTtsVoice { get; set; } = "ono_anna";
    public string QwenTtsDevice { get; set; } = "auto";
    internal bool UsesExpressiveVoice => TtsEngine == "qwen";
    public double SpeechRate { get; set; } = 1;
    public int VoiceSpeakerId { get; set; } = 0;
    public double SpeakerMatchThreshold { get; set; } = 0.12;
    public bool SpeakerRecognitionEnabled { get; set; } = true;
    public int ProcessTimeoutSeconds { get; set; } = 180;
}

public sealed record CapturedSpeech(string Text, string SpeakerId, double SpeakerConfidence, bool IsKnownSpeaker);

/// <summary>Portable process adapters and a native PCM recorder. The microphone is opened only while PTT is held.</summary>
public sealed class AudioService : IAsyncDisposable
{
    private readonly string root;
    private readonly AudioOptions options;
    private readonly string temporaryDirectory;
    private readonly SpeakerProfiles speakers;
    private readonly SemaphoreSlim speechGate = new(1, 1);
    private readonly SemaphoreSlim listenGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();

    public AudioService(string root, AudioOptions options)
    {
        this.root = Path.GetFullPath(root); this.options = options;
        temporaryDirectory = Path.Combine(this.root, "data", "tmp", "audio");
        speakers = new SpeakerProfiles(Path.Combine(this.root, "data", "speakers"), options.SpeakerMatchThreshold);
    }

    public void EnrollSpeaker(string id) => speakers.EnrollNext(id);

    internal async Task WaitForInputIdleAsync(CancellationToken ct)
    {
        await listenGate.WaitAsync(ct);
        listenGate.Release();
    }

    public string DescribeDevices()
    {
        if (!OperatingSystem.IsWindows()) return "Native audio requires Windows.";
        var text = new StringBuilder("Input devices (-1 = Windows default):\n");
        for (uint i = 0; i < NativeAudio.waveInGetNumDevs(); i++)
            if (NativeAudio.waveInGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<NativeAudio.DeviceCaps>()) == 0) text.AppendLine($"  {i}: {caps.Name}");
        text.AppendLine("Output devices (-1 = Windows default):");
        for (uint i = 0; i < NativeAudio.waveOutGetNumDevs(); i++)
            if (NativeAudio.waveOutGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<NativeAudio.DeviceCaps>()) == 0) text.AppendLine($"  {i}: {caps.Name}");
        return text.ToString();
    }

    public async Task<CapturedSpeech?> ListenWhileHeldAsync(Func<bool> held, CancellationToken cancellationToken)
    {
        if (!options.Enabled || !held()) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Microphone capture requires Windows.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        await listenGate.WaitAsync(token);
        string? wavePath = null, textPath = null;
        try
        {
            if (!held()) return null;
            // Whisper's CLI expects 16 kHz PCM. Fail before opening a device rather than silently resampling badly.
            if (options.SampleRate != 16000) throw new InvalidOperationException("Whisper recording SampleRate must be 16000.");
            var executable = ResolveInside(options.WhisperExecutable);
            var model = ResolveInside(options.WhisperModel);
            RequireFile(executable, "whisper-cli"); RequireFile(model, "multilingual Whisper model");
            var pcm = await NativeAudio.RecordAsync(options.InputDeviceId, options.SampleRate, Math.Clamp(options.MaxRecordingSeconds, 1, 300), held, token);
            if (pcm.Length < options.SampleRate * 2 * 0.35 || Rms(pcm) < 0.004) return null;
            Directory.CreateDirectory(temporaryDirectory);
            var stem = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
            wavePath = stem + ".wav"; textPath = stem + ".txt";
            await File.WriteAllBytesAsync(wavePath, NativeAudio.CreateWave(pcm, options.SampleRate), token);
            await RunAsync(executable, ["--model", model, "--file", wavePath, "--language", "auto", "--output-txt", "--output-file", stem, "--no-timestamps", "--no-prints"], null, token);
            if (!File.Exists(textPath)) throw new IOException("Whisper did not create a transcript. Check data/logs and the multilingual model.");
            var text = (await File.ReadAllTextAsync(textPath, token)).Trim();
            if (string.IsNullOrWhiteSpace(text) || text is "[BLANK_AUDIO]" or "[Silence]" or "[silence]") return null;
            var identity = options.SpeakerRecognitionEnabled ? await speakers.IdentifyAsync(pcm, options.SampleRate, token) : new SpeakerIdentity("unknown", 0, false);
            return new CapturedSpeech(text, identity.Id, identity.Confidence, identity.Known);
        }
        finally
        {
            TryDelete(wavePath); TryDelete(textPath); listenGate.Release();
        }
    }

    internal static string PrepareSpeechText(string text)
    {
        text=Regex.Replace(text,@"\[([^\]]+)\]\([^\)]+\)","$1");
        text=Regex.Replace(text,@"(?m)^\s*#{1,6}\s*","");
        text=text.Replace("**","").Replace("__","").Replace("`","");
        return text[..Math.Min(12000, text.Length)].Trim();
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        !options.Enabled || string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : SpeakPreparedAsync(PrepareSpeechText(text), [], cancellationToken);

    internal async Task SpeakPreparedAsync(string text, IReadOnlyList<SpeechSentence> sentences, CancellationToken cancellationToken,
        Action<string>? reportVoiceFailure = null)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(text)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Audio playback requires Windows.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        await speechGate.WaitAsync(token);
        try
        {
            if (!options.UsesExpressiveVoice)
            {
                await NativeAudio.PlayAsync(await SynthesizePiperAsync(text, token), options.OutputDeviceId, token);
                return;
            }

            var blocks = GroupSpeech(sentences.Count == 0 ? [new(text)] : sentences).ToArray();
            await PlayExpressiveAsync(blocks, reportVoiceFailure, token);
        }
        finally { speechGate.Release(); }
    }

    private async Task PlayExpressiveAsync(SpeechSentence[] blocks, Action<string>? reportVoiceFailure, CancellationToken token)
    {
        var played = 0;
        Exception? failure = null;
        try
        {
            var paths = new PortablePaths(root);
            var executable = paths.Inside(options.QwenTtsExecutable);
            var model = paths.Inside(options.QwenTtsModel);
            RequireFile(executable, "speech.cpp"); RequireFile(model, "Qwen3-TTS voice");
            var modelBytes = (ulong)new FileInfo(model).Length;
            // Each CPU lane loads its own weights. Leave room for two models, their working buffers and Windows.
            var lanes = blocks.Length > 1 && options.QwenTtsDevice == "cpu" && Environment.ProcessorCount >= 8
                && SystemContext.HasMemoryHeadroom(2 * modelBytes + 1_500_000_000UL) ? 2 : 1;
            var threads = Math.Clamp(Environment.ProcessorCount / (2 * lanes), 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var arguments = new[] { "worker", model, "--device", options.QwenTtsDevice, "--threads", threads,
                "--voice", options.QwenTtsVoice, "--language", "en", "--no-warmup" };

            failure = await SpeechQueue.PlayAsync(blocks.Length, lanes,
                () => new QwenVoiceWorker(CreateStartInfo(executable, arguments, redirectInput: true), options.ProcessTimeoutSeconds),
                async (worker, index, queueToken) =>
                {
                    var block = blocks[index];
                    var spoken = block with { Text = SpeechDelivery.WithSentenceEnding(block.Text) };
                    var audio = await worker.SynthesizeAsync(spoken, retry: false, queueToken);
                    if (audio.HasAbruptEnding())
                    {
                        audio = await worker.SynthesizeAsync(spoken, retry: true, queueToken);
                        if (audio.HasAbruptEnding()) audio = await SynthesizePiperAsync(block.Text, queueToken);
                    }
                    return audio;
                }, async (audio, queueToken) =>
                {
                    await NativeAudio.PlayAsync(audio, options.OutputDeviceId, queueToken);
                    played++;
                }, token);
        }
        catch (Exception ex) when (played == 0 && ex is FileNotFoundException)
        {
            failure = ex;
        }
        if (failure is null) return;

        token.ThrowIfCancellationRequested();
        if (!IsSynthesisFailure(failure) && failure is not OperationCanceledException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        reportVoiceFailure?.Invoke(failure is OperationCanceledException ? "Qwen3-TTS timed out. Switching to Piper for this reply." :
            $"Qwen3-TTS failed. Switching to Piper for this reply. {failure.Message}");
        var remaining = string.Join(" ", blocks.Skip(played).Select(block => block.Text));
        await NativeAudio.PlayAsync(await SynthesizePiperAsync(remaining, token), options.OutputDeviceId, token);
    }

    private static bool IsSynthesisFailure(Exception ex) => ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception
        or JsonException or KeyNotFoundException or InvalidOperationException or FormatException;

    private async Task<NativeAudio.PcmAudio> SynthesizePiperAsync(string text, CancellationToken token)
    {
        var executable = ResolveInside(options.PiperExecutable); var model = ResolveInside(options.PiperModel);
        var config = model + ".json";
        RequireFile(executable, "Piper"); RequireFile(model, "Piper voice"); RequireFile(config, "Piper voice metadata");

        // Piper's raw pipe carries mono PCM16. The rate comes from the voice, with Piper's default when omitted.
        using var voice = JsonDocument.Parse(await File.ReadAllTextAsync(config, token));
        var sampleRate = 22050;
        if (voice.RootElement.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object
            && audio.TryGetProperty("sample_rate", out var rateSetting))
            sampleRate = rateSetting.GetInt32();
        if (sampleRate is < 8000 or > 192000) throw new InvalidDataException("Invalid Piper sample rate.");

        var rate = Math.Clamp(options.SpeechRate, 0.5, 2.0);
        using var pcm = new MemoryStream();
        await RunAsync(executable, ["--model", model, "--config", config, "--output_raw",
            "--speaker", Math.Max(0, options.VoiceSpeakerId).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--length_scale", (1 / rate).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            text.ReplaceLineEndings(" "), token, pcm);
        if (pcm.Length == 0 || pcm.Length % 2 != 0) throw new InvalidDataException("Piper returned incomplete PCM16 audio.");
        return new(pcm.ToArray(), sampleRate, 1);
    }

    private static IEnumerable<SpeechSentence> GroupSpeech(IReadOnlyList<SpeechSentence> sentences)
    {
        // The first sentence can play as soon as it is ready, even when the rest keeps the same tone.
        yield return sentences[0];
        if (sentences.Count < 2) yield break;
        var block = sentences[1];
        foreach (var sentence in sentences.Skip(2))
        {
            if (sentence.Emotion == block.Emotion && block.Text.Length + sentence.Text.Length < 250)
            {
                block = block with { Text = block.Text + " " + sentence.Text };
                continue;
            }
            yield return block;
            block = sentence;
        }
        yield return block;
    }

    private async Task RunAsync(string executable, IReadOnlyList<string> arguments, string? input, CancellationToken token,
        MemoryStream? pcm = null)
    {
        token.ThrowIfCancellationRequested();
        var start = CreateStartInfo(executable, arguments, redirectInput: input != null);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        ChildProcessJob.Attach(process);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ProcessTimeoutSeconds, 10, 1800)));
        // Drain both streams immediately; noisy model loaders must not deadlock on a full pipe.
        Task outputTask = pcm is null ? DrainBoundedAsync(process.StandardOutput, timeout.Token)
            : CopyPcmAsync(process.StandardOutput.BaseStream, pcm, timeout.Token);
        var errorTask = DrainBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            if (input != null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await outputTask;
            await process.WaitForExitAsync(timeout.Token);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error}");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            try { await Task.WhenAll(outputTask, errorTask); } catch { /* Keep the original process failure. */ }
            throw;
        }
    }

    private static async Task CopyPcmAsync(Stream source, MemoryStream destination, CancellationToken token)
    {
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (destination.Length + count > 100_000_000) throw new InvalidDataException("Speech output is too large.");
            destination.Write(buffer, 0, count);
        }
    }

    private ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (redirectInput) start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temporaryDirectory; start.Environment["TMP"] = temporaryDirectory;
        return start;
    }

    internal static async Task<string> DrainBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var tail = new StringBuilder(); var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
        }
        return tail.ToString();
    }
    private string ResolveInside(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Audio executables and models must stay inside ComputerNeighbour.");
        return path;
    }
    private static void RequireFile(string path, string description) { if (!File.Exists(path)) throw new FileNotFoundException($"Missing {description}: {path}. Run Setup.cmd first.", path); }
    private static void TryDelete(string? path) { try { if (path != null) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static double Rms(byte[] pcm)
    {
        double sum = 0; for (var i = 0; i + 1 < pcm.Length; i += 2) { var sample = (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768d; sum += sample * sample; }
        return Math.Sqrt(sum / Math.Max(1, pcm.Length / 2));
    }
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        // Wait for capture/playback cleanup before disposing synchronization primitives.
        await listenGate.WaitAsync(); await speechGate.WaitAsync();
        listenGate.Release(); speechGate.Release(); stopping.Dispose();
        listenGate.Dispose(); speechGate.Dispose();
    }
}
