using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(text)) return;
        text=Regex.Replace(text,@"\[([^\]]+)\]\([^\)]+\)","$1");
        text=Regex.Replace(text,@"(?m)^\s*#{1,6}\s*","");
        text=text.Replace("**","").Replace("__","").Replace("`","");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Audio playback requires Windows.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        await speechGate.WaitAsync(token);
        string? wavePath = null;
        try
        {
            var executable = ResolveInside(options.PiperExecutable); var model = ResolveInside(options.PiperModel);
            RequireFile(executable, "Piper"); RequireFile(model, "Piper voice"); RequireFile(model + ".json", "Piper voice metadata");
            Directory.CreateDirectory(temporaryDirectory);
            wavePath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".wav");
            var rate = Math.Clamp(options.SpeechRate, 0.5, 2.0);
            // Piper 2023 CLI. The portable setup pins this release, including its neural English voice.
            await RunAsync(executable, ["--model", model, "--config", model + ".json", "--output_file", wavePath, "--speaker", Math.Max(0, options.VoiceSpeakerId).ToString(System.Globalization.CultureInfo.InvariantCulture), "--length_scale", (1 / rate).ToString(System.Globalization.CultureInfo.InvariantCulture)], text.Length > 12000 ? text[..12000] : text, token);
            RequireFile(wavePath, "generated speech");
            await NativeAudio.PlayAsync(wavePath, options.OutputDeviceId, token);
        }
        finally { TryDelete(wavePath); speechGate.Release(); }
    }

    private async Task RunAsync(string executable, IReadOnlyList<string> arguments, string? input, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temporaryDirectory; start.Environment["TMP"] = temporaryDirectory;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        ChildProcessJob.Attach(process);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ProcessTimeoutSeconds, 10, 1800)));
        // Drain both streams immediately; noisy model loaders must not deadlock on a full pipe.
        var outputTask = DrainBoundedAsync(process.StandardOutput, timeout.Token);
        var errorTask = DrainBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            if (input != null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            await outputTask; var error = await errorTask;
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error}");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(outputTask, errorTask); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader, CancellationToken token)
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
