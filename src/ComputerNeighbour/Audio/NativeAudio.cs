using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerNeighbour.Audio;

internal static class NativeAudio
{
    private const uint HeaderDone = 1;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

    internal sealed record PcmAudio(byte[] Samples, int SampleRate, ushort Channels)
    {
        // A sustained signal at the very last sample can indicate an early codec stop.
        // Natural fades and quiet final consonants should pass without another generation.
        internal bool HasAbruptEnding()
        {
            var last = SampleRate * Channels * 12 / 1000 * 2;
            var window = SampleRate * Channels * 80 / 1000 * 2;
            if (last < 2 || Samples.Length < last + window) return false;
            var edge = MeanSquare(Samples.AsSpan(Samples.Length - last));
            return edge > 600 * 600 && edge > MeanSquare(Samples.AsSpan(Samples.Length - last - window, window)) * 0.45;
        }

        private static double MeanSquare(ReadOnlySpan<byte> pcm)
        {
            double sum = 0;
            for (var i = 0; i < pcm.Length; i += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
                sum += sample * sample;
            }
            return sum / (pcm.Length / 2);
        }
    }

    internal static async Task<byte[]> RecordAsync(int device, int sampleRate, int maxSeconds, Func<bool> held, CancellationToken token)
    {
        var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = (uint)sampleRate, AverageBytesPerSecond = (uint)(sampleRate * 2), BlockAlign = 2, BitsPerSample = 16 };
        Check(waveInOpen(out var handle, unchecked((uint)device), ref format, IntPtr.Zero, UIntPtr.Zero, 0), "open microphone");
        var buffers = new List<NativeBuffer>();
        using var captured = new MemoryStream();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var nextBuffer = 0;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var buffer = new NativeBuffer(sampleRate / 10 * 2); buffers.Add(buffer);
                Check(waveInPrepareHeader(handle, buffer.Header, HeaderSize), "prepare microphone buffer"); buffer.Prepared = true;
                Check(waveInAddBuffer(handle, buffer.Header, HeaderSize), "queue microphone buffer");
            }
            if (!held()) return [];
            Check(waveInStart(handle), "start microphone"); elapsed.Restart();
            while (held() && elapsed.Elapsed.TotalSeconds < maxSeconds)
            {
                token.ThrowIfCancellationRequested();
                for (var processed = 0; processed < buffers.Count; processed++)
                {
                    var buffer = buffers[nextBuffer];
                    var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                    if ((header.Flags & HeaderDone) == 0) break;
                    AppendAndClear(captured, buffer.Header);
                    nextBuffer = (nextBuffer + 1) % buffers.Count;
                    if (!held()) break;
                    Check(waveInAddBuffer(handle, buffer.Header, HeaderSize), "requeue microphone buffer");
                }
                // Once a release is observed, stop immediately instead of sleeping with the device open.
                if (!held()) break;
                await Task.Delay(10, token);
            }
            var heldSeconds = elapsed.Elapsed.TotalSeconds;
            Check(waveInStop(handle), "stop microphone");
            Check(waveInReset(handle), "reset microphone");
            for (var offset = 0; offset < buffers.Count; offset++)
            {
                var buffer = buffers[(nextBuffer + offset) % buffers.Count];
                var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                if ((header.Flags & HeaderDone) != 0) AppendAndClear(captured, buffer.Header);
            }
            // Key polling and reset take a few milliseconds; never keep samples beyond the observed held interval.
            var limit = Math.Min(captured.Length, (long)Math.Floor(Math.Min(heldSeconds, maxSeconds) * sampleRate) * 2);
            return captured.ToArray().AsSpan(0, (int)(limit & ~1L)).ToArray();
        }
        finally
        {
            waveInStop(handle); waveInReset(handle);
            foreach (var buffer in buffers) if (buffer.Prepared) waveInUnprepareHeader(handle, buffer.Header, HeaderSize);
            waveInClose(handle);
            foreach (var buffer in buffers) buffer.Dispose();
        }
    }

    // Requeued headers have their byte count cleared by Windows. Clear consumed counts too so stop/reset cannot duplicate an already consumed final buffer.
    private static void AppendAndClear(Stream stream, IntPtr headerPointer)
    {
        var header = Marshal.PtrToStructure<WaveHeader>(headerPointer);
        if (header.BytesRecorded == 0) return;
        var bytes = new byte[Math.Min(header.BytesRecorded, header.BufferLength)]; Marshal.Copy(header.Data, bytes, 0, bytes.Length); stream.Write(bytes);
        header.BytesRecorded = 0;
        Marshal.StructureToPtr(header, headerPointer, false);
    }

    internal static byte[] CreateWave(byte[] pcm, int sampleRate)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm); return stream.ToArray();
    }

    internal static async Task PlayAsync(PcmAudio audio, int device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var align = (ushort)(audio.Channels * 2);
        var format = new WaveFormat { FormatTag = 1, Channels = audio.Channels, SamplesPerSecond = (uint)audio.SampleRate,
            AverageBytesPerSecond = (uint)audio.SampleRate * align, BlockAlign = align, BitsPerSample = 16 };
        // Keep the device open through a short silent tail so buffered speech reaches the speakers.
        var silence = new byte[audio.SampleRate * 120 / 1000 * align];
        using var buffer = new NativeBuffer(audio.Samples.Length + silence.Length);
        Check(waveOutOpen(out var handle, unchecked((uint)device), ref format, IntPtr.Zero, UIntPtr.Zero, 0), "open playback device");
        var playing = false;
        try
        {
            Marshal.Copy(audio.Samples, 0, buffer.Data, audio.Samples.Length);
            Marshal.Copy(silence, 0, IntPtr.Add(buffer.Data, audio.Samples.Length), silence.Length);
            Check(waveOutPrepareHeader(handle, buffer.Header, HeaderSize), "prepare playback"); buffer.Prepared = true;
            Check(waveOutWrite(handle, buffer.Header, HeaderSize), "start playback");
            playing = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds((audio.Samples.Length + silence.Length) / (double)format.AverageBytesPerSecond + 15));
            while ((Marshal.PtrToStructure<WaveHeader>(buffer.Header).Flags & HeaderDone) == 0) await Task.Delay(20, timeout.Token);
            playing = false;
        }
        finally
        {
            if (playing) waveOutReset(handle);
            if (buffer.Prepared) waveOutUnprepareHeader(handle, buffer.Header, HeaderSize);
            waveOutClose(handle);
        }
    }

    private static void Check(uint result, string action) { if (result != 0) throw new Win32Exception((int)result, $"Could not {action} (Windows multimedia error {result}). Check the selected device and microphone permission in Windows."); }
    private sealed class NativeBuffer : IDisposable
    {
        public IntPtr Data { get; }
        public IntPtr Header { get; }
        public bool Prepared { get; set; }
        public NativeBuffer(int size) { Data = Marshal.AllocHGlobal(size); Header = Marshal.AllocHGlobal((int)HeaderSize); Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)size }, Header, false); }
        public void Dispose() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] internal struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DeviceCaps { public ushort Manufacturer, Product; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr input, uint device, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr input, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr input, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr input, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr input);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr input);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr input);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr input);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr output, uint device, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr output);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr output);
    [DllImport("winmm.dll")] internal static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] internal static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern uint waveInGetDevCaps(UIntPtr device, out DeviceCaps caps, uint size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern uint waveOutGetDevCaps(UIntPtr device, out DeviceCaps caps, uint size);
}
