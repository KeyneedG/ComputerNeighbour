using System.Numerics;
using System.Text.Json;

namespace ComputerNeighbour.Audio;

internal sealed record SpeakerIdentity(string Id, double Confidence, bool Known);

/// <summary>
/// Lightweight local MFCC matching for explicitly enrolled voices. This is an acoustic aid, not
/// biometric authentication or neural diarization. Short, noisy, mixed, and ambiguous input remains unknown.
/// </summary>
internal sealed class SpeakerProfiles(string directory, double threshold)
{
    private string? pendingEnrollment;
    private sealed record Profile(string Id, double[] Features, DateTimeOffset UpdatedAt, string Method = "mfcc-v1");

    public void EnrollNext(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Speaker IDs must contain 1–64 letters, numbers, underscores, or hyphens.");
        Interlocked.Exchange(ref pendingEnrollment, id);
    }

    public async Task<SpeakerIdentity> IdentifyAsync(byte[] pcm, int sampleRate, CancellationToken token)
    {
        var features = Extract(pcm, sampleRate, out var voicedSeconds);
        if (features == null || voicedSeconds < 1.2) return new SpeakerIdentity("unknown", 0, false);
        var enrollment = Interlocked.Exchange(ref pendingEnrollment, null);
        Directory.CreateDirectory(directory);
        if (enrollment != null)
        {
            if (voicedSeconds < 2.0)
            {
                Interlocked.CompareExchange(ref pendingEnrollment, enrollment, null);
                return new SpeakerIdentity("unknown-enrollment-needs-longer-speech", 0, false);
            }
            var path = Path.Combine(directory, enrollment + ".json");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Profile(enrollment, features, DateTimeOffset.UtcNow), new JsonSerializerOptions { WriteIndented = true }), token);
            File.Move(temporary, path, true);
            return new SpeakerIdentity(enrollment, 1, true);
        }
        var candidates = new List<(string Id, double Distance)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(256))
        {
            try
            {
                if (new FileInfo(path).Length > 65536) continue;
                var profile = JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(path, token));
                if (profile?.Features is { } stored && stored.Length == features.Length && profile.Method == "mfcc-v1" && profile.Features.All(double.IsFinite)
                    && profile.Id == Path.GetFileNameWithoutExtension(path) && profile.Id.Length is >= 1 and <= 64
                    && profile.Id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                    candidates.Add((profile.Id, Distance(features, profile.Features)));
            }
            catch (JsonException) { /* A damaged enrollment must not identify an unrelated speaker. */ }
            catch (IOException) { }
        }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var limit = Math.Clamp(threshold, 0.005, 0.35);
        if (candidates.Count == 0 || candidates[0].Distance > limit || (candidates.Count > 1 && candidates[1].Distance - candidates[0].Distance < 0.035))
            return new SpeakerIdentity("unknown", 0, false);
        // Reject strongly changing timbre across a longer recording. This cannot reliably segment two overlapping voices.
        if (pcm.Length >= sampleRate * 2 * 5)
        {
            var half = (pcm.Length / 4) * 2;
            var first = Extract(pcm.AsSpan(0, half).ToArray(), sampleRate, out _);
            var last = Extract(pcm.AsSpan(half).ToArray(), sampleRate, out _);
            if (first != null && last != null && Distance(first, last) > limit * 1.8) return new SpeakerIdentity("unknown-or-mixed", 0, false);
        }
        return new SpeakerIdentity(candidates[0].Id, Math.Clamp(1 - candidates[0].Distance / limit, 0, 0.99), true);
    }

    private static double Distance(double[] left, double[] right)
    {
        double dot = 0, ll = 0, rr = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; ll += left[i] * left[i]; rr += right[i] * right[i]; }
        return 1 - dot / Math.Max(1e-12, Math.Sqrt(ll * rr));
    }

    private static double[]? Extract(byte[] pcm, int sampleRate, out double voicedSeconds)
    {
        const int fftSize = 512, filters = 24, coefficients = 12;
        var frameLength = sampleRate / 40; var step = sampleRate / 100;
        var sampleCount = pcm.Length / 2;
        var samples = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++) samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768d;
        var sums = new double[coefficients]; var squares = new double[coefficients]; var frames = 0;
        var bins = new int[filters + 2];
        static double Mel(double hz) => 2595 * Math.Log10(1 + hz / 700);
        var low = Mel(80); var high = Mel(Math.Min(7600, sampleRate / 2d));
        for (var i = 0; i < bins.Length; i++) bins[i] = Math.Clamp((int)Math.Floor((fftSize + 1) * 700 * (Math.Pow(10, (low + (high - low) * i / (filters + 1)) / 2595) - 1) / sampleRate), 0, fftSize / 2);
        for (var start = 0; start + frameLength <= sampleCount; start += step)
        {
            double energy = 0; for (var i = 0; i < frameLength; i++) energy += samples[start + i] * samples[start + i];
            if (energy / frameLength < 0.00004) continue;
            var spectrum = new Complex[fftSize];
            for (var i = 0; i < frameLength; i++) spectrum[i] = (samples[start + i] - (start + i > 0 ? 0.97 * samples[start + i - 1] : 0)) * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frameLength - 1)));
            Fft(spectrum);
            var logEnergies = new double[filters];
            for (var filter = 0; filter < filters; filter++)
            {
                double sum = 0;
                for (var bin = bins[filter]; bin < bins[filter + 1]; bin++) sum += spectrum[bin].Magnitude * spectrum[bin].Magnitude * (bin - bins[filter]) / Math.Max(1d, bins[filter + 1] - bins[filter]);
                for (var bin = bins[filter + 1]; bin < bins[filter + 2]; bin++) sum += spectrum[bin].Magnitude * spectrum[bin].Magnitude * (bins[filter + 2] - bin) / Math.Max(1d, bins[filter + 2] - bins[filter + 1]);
                logEnergies[filter] = Math.Log(Math.Max(sum, 1e-10));
            }
            // Skip the energy coefficient, which would primarily identify microphone volume.
            for (var coefficient = 1; coefficient <= coefficients; coefficient++)
            {
                double value = 0; for (var filter = 0; filter < filters; filter++) value += logEnergies[filter] * Math.Cos(Math.PI * coefficient * (filter + 0.5) / filters);
                value *= Math.Sqrt(2d / filters);
                sums[coefficient - 1] += value; squares[coefficient - 1] += value * value;
            }
            frames++;
        }
        voicedSeconds = frames * step / (double)sampleRate;
        if (frames < 60) return null;
        var result = new double[coefficients * 2];
        for (var i = 0; i < coefficients; i++) { result[i] = sums[i] / frames; result[coefficients + i] = Math.Sqrt(Math.Max(0, squares[i] / frames - result[i] * result[i])); }
        return result;
    }

    private static void Fft(Complex[] data)
    {
        var length = data.Length;
        for (int i = 1, j = 0; i < length; i++)
        {
            var bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit; if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = -2 * Math.PI / size; var rotation = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += size)
            {
                var factor = Complex.One;
                for (var offset = 0; offset < size / 2; offset++)
                {
                    var even = data[start + offset]; var odd = factor * data[start + offset + size / 2];
                    data[start + offset] = even + odd; data[start + offset + size / 2] = even - odd; factor *= rotation;
                }
            }
        }
    }
}
