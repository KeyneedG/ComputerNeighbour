using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerNeighbour.Audio;

internal static class NativeAudio
{
    private const uint HeaderDone = 1;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

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

    internal static async Task PlayAsync(string file, int device, CancellationToken token)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Piper output is not a RIFF wave file.");
        reader.ReadUInt32(); if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Piper output is not a wave file.");
        WaveFormat format = default; byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var kind = Encoding.ASCII.GetString(reader.ReadBytes(4)); var length = reader.ReadUInt32();
            var start = reader.BaseStream.Position;
            if (length > reader.BaseStream.Length - start) throw new InvalidDataException("Truncated wave chunk.");
            if (kind == "fmt " && length >= 16)
            {
                format = new WaveFormat { FormatTag = reader.ReadUInt16(), Channels = reader.ReadUInt16(), SamplesPerSecond = reader.ReadUInt32(), AverageBytesPerSecond = reader.ReadUInt32(), BlockAlign = reader.ReadUInt16(), BitsPerSample = reader.ReadUInt16() };
            }
            else if (kind == "data") { if (length > 100_000_000) throw new InvalidDataException("Speech file too large."); data = reader.ReadBytes((int)length); }
            reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, start + length + (length & 1));
        }
        if (data == null || data.Length == 0 || format.FormatTag != 1 || format.Channels is < 1 or > 2 || format.BitsPerSample != 16 || format.AverageBytesPerSecond == 0) throw new InvalidDataException("Piper must produce PCM16 mono/stereo audio.");
        Check(waveOutOpen(out var handle, unchecked((uint)device), ref format, IntPtr.Zero, UIntPtr.Zero, 0), "open playback device");
        using var buffer = new NativeBuffer(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer.Data, data.Length);
            Check(waveOutPrepareHeader(handle, buffer.Header, HeaderSize), "prepare playback"); buffer.Prepared = true;
            Check(waveOutWrite(handle, buffer.Header, HeaderSize), "start playback");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(data.Length / (double)format.AverageBytesPerSecond + 15));
            while ((Marshal.PtrToStructure<WaveHeader>(buffer.Header).Flags & HeaderDone) == 0) await Task.Delay(20, timeout.Token);
        }
        finally
        {
            waveOutReset(handle);
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
