using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerNeighbour.Platform;

public sealed class PlatformOptions
{
    public int ScreenMaxWidth { get; set; } = 1280;
    public List<string> ExcludedWindowTitles { get; set; } = ["password", "парол", "private browsing", "incognito", "inprivate", "1password", "bitwarden", "keepass"];
}

/// <summary>Small Windows adapter. All handles are scoped; no hooks, services, or registry writes.</summary>
public sealed class WindowsDesktop(PlatformOptions options)
{
    private ulong previousSystemIdle,previousSystemTotal;
    private long previousCpuSampleAt;

    public string GetForegroundContext()
    {
        EnsureWindows();
        var window = GetForegroundWindow();
        var title = new StringBuilder(1024);
        GetWindowText(window, title, title.Capacity);
        if (IsExcluded(title.ToString())) return "[Excluded private window: observation paused]";
        GetWindowThreadProcessId(window, out var processId);
        string name;
        try { using var process = Process.GetProcessById((int)processId); name = process.ProcessName; }
        catch { name = "unknown"; }
        return $"Application: {name}; window: {title}";
    }

    public TimeSpan GetIdleTime()=>ReadIdleTime()??TimeSpan.Zero;

    public TimeSpan? GetInteractiveIdleTime()=>IsInteractiveDesktop()?ReadIdleTime():null;

    private static TimeSpan? ReadIdleTime()
    {
        EnsureWindows();
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick)) : null;
    }

    public bool CanUseSketchWorkspace(bool checkLoad,double maxSampleSeconds=15)
    {
        if(!IsInteractiveDesktop())return false;
        var foreground=GetForegroundWindow();
        var title=new StringBuilder(1024);
        GetWindowText(foreground,title,title.Capacity);
        if(IsExcluded(title.ToString()))return false;
        GetWindowThreadProcessId(foreground,out var pid);
        try
        {
            using var process=Process.GetProcessById((int)pid);
            var foregroundName=process.ProcessName.ToLowerInvariant();
            if(foregroundName is not ("explorer" or "windowsterminal" or "openconsole" or "conhost" or "computerneighbour" or "cmd" or "powershell" or "pwsh"))return false;
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or Win32Exception){return false;}
        return !checkLoad||SystemIsQuiet(maxSampleSeconds);
    }

    private bool SystemIsQuiet(double maxSampleSeconds)
    {
        if(!GetSystemTimes(out var idle,out var kernel,out var user))return false;
        var total=kernel+user;
        var oldIdle=previousSystemIdle;
        var oldTotal=previousSystemTotal;
        var now=Environment.TickCount64;
        var sampleAge=now-previousCpuSampleAt;
        previousCpuSampleAt=now;
        previousSystemIdle=idle;previousSystemTotal=total;
        return sampleAge>=250&&sampleAge<=maxSampleSeconds*1000&&oldTotal!=0&&total>oldTotal&&idle>=oldIdle
            &&(idle-oldIdle)/(double)(total-oldTotal)>=.75;
    }

    private static bool IsInteractiveDesktop()
    {
        if(!OperatingSystem.IsWindows()||GetForegroundWindow()==IntPtr.Zero)return false;
        var input=OpenInputDesktop(0,false,1); // DESKTOP_READOBJECTS
        if(input==IntPtr.Zero)return false;
        try
        {
            var name=new StringBuilder(128);
            return GetUserObjectInformation(input,2,name,(uint)(name.Capacity*sizeof(char)),out _)&&name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase);
        }
        finally{CloseDesktop(input);}
    }

    internal static bool TryOpenPaint(string image)
    {
        EnsureWindows();
        var existing=Process.GetProcessesByName("mspaint");
        foreach(var process in existing)process.Dispose();
        var exe=Path.Combine(Environment.SystemDirectory,"mspaint.exe");
        if(existing.Length>0||!File.Exists(exe))return false;
        var start=new ProcessStartInfo(exe){UseShellExecute=false};
        start.ArgumentList.Add(image);
        using var paint=Process.Start(start);
        return paint is not null;
    }

    public bool IsKeyDown(int virtualKey) => OperatingSystem.IsWindows() && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public byte[] CaptureScreenPng()
    {
        EnsureWindows();
        var title = new StringBuilder(1024);
        GetWindowText(GetForegroundWindow(), title, title.Capacity);
        if (IsExcluded(title.ToString())) throw new InvalidOperationException("Screen observation is paused for this private window.");
        var sourceWidth = GetSystemMetrics(0);
        var sourceHeight = GetSystemMetrics(1);
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("No interactive desktop is available.");
        var width = Math.Min(sourceWidth, Math.Clamp(options.ScreenMaxWidth, 320, 3840));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * (double)width / sourceWidth));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception();
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception();
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            SetStretchBltMode(memory, 4); // HALFTONE: readable downscaled text.
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memory, 0, 0, width, height, screen, 0, 0, sourceWidth, sourceHeight, 0x00CC0020)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not capture the interactive desktop. A locked screen or restricted session cannot be observed.");
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            var pixels = new byte[checked(width * height * 4)];
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length } };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new Win32Exception();
            return EncodePng(pixels, width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Click(int x, int y)
    {
        EnsureWindows();
        if (x < 0 || y < 0 || x >= GetSystemMetrics(0) || y >= GetSystemMetrics(1)) throw new ArgumentOutOfRangeException(nameof(x), "Click must be inside the primary screen in physical pixels.");
        if (!SetCursorPos(x, y)) throw new Win32Exception();
        Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } }, new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0004 } } }]);
    }

    public void TypeText(string text)
    {
        EnsureWindows();
        if (text.Length > 10000) throw new ArgumentException("Text input is limited to 10,000 UTF-16 characters.");
        foreach (var character in text)
            Send([KeyInput(0, character, 0x0004), KeyInput(0, character, 0x0004 | 0x0002)]);
    }

    public void PressKey(string key)
    {
        EnsureWindows();
        var parts = key.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) throw new ArgumentException("Use a key or a combination such as CTRL+L.");
        var keys = parts.Select(ParseKey).ToArray();
        var inputs = keys.Select(v => KeyInput(v, '\0', 0)).Concat(keys.Reverse().Select(v => KeyInput(v, '\0', 0x0002))).ToArray();
        Send(inputs);
    }

    public void OpenAllowedApp(string executable)
    {
        EnsureWindows();
        // The caller resolves an explicitly allowlisted executable. Never pass this to a shell.
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Invalid application path.");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
    }

    private bool IsExcluded(string title) => (options.ExcludedWindowTitles ?? []).Any(x => !string.IsNullOrWhiteSpace(x) && title.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The desktop adapter requires Windows."); }
    private static ushort ParseKey(string value)
    {
        var key = value.ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 24) return (ushort)(0x70 + function - 1);
        return key switch { "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10, "WIN" => 0x5B, "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B, "SPACE" => 0x20, "BACKSPACE" => 0x08, "DELETE" or "DEL" => 0x2E, "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, _ => throw new ArgumentException($"Unsupported key: {value}") };
    }
    private static Input KeyInput(ushort key, char character, uint flags) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Scan = character, Flags = flags } } };
    private static void Send(Input[] inputs) { if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) throw new Win32Exception("Windows rejected input; elevated applications cannot be controlled from a normal process."); }

    internal static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4; var target = 1 + x * 3;
                    row[target] = bgra[source + 2]; row[target + 1] = bgra[source + 1]; row[target + 2] = bgra[source];
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray()); WriteChunk(output, "IEND", []);
        return output.ToArray();
    }
    private static void WriteChunk(Stream target, string kind, byte[] bytes)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); target.Write(number);
        var name = Encoding.ASCII.GetBytes(kind); target.Write(name); target.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var value in name.Concat(bytes)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); target.Write(number);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder result,uint bytes,out uint needed);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr value);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint count, [Out] byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
