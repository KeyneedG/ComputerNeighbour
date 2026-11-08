using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerNeighbour.Platform;

internal sealed record WorkWindow(nint Handle, nint Owner, uint ProcessId, string Process, string Title,
    int Left, int Top, int Width, int Height, nint FocusedControl = 0)
{
    internal bool SameTarget(WorkWindow other) => ProcessId == other.ProcessId && Owner == other.Owner;
}

internal sealed record WorkFrame(WorkWindow Window, byte[] Image, int Width, int Height)
{
    internal (int X, int Y) ScreenPoint(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x), "Use a pixel inside the latest window image.");
        return (Window.Left + (int)((long)x * Window.Width / Width),
            Window.Top + (int)((long)y * Window.Height / Height));
    }
}

internal interface IWorkDesktop
{
    WorkWindow CurrentWindow();
    WorkFrame Observe(WorkWindow window);
    void Focus(string executable);
    void Restore(WorkWindow window);
    void Click(WorkFrame frame, int x, int y);
    void Type(WorkFrame frame, string text);
    void Key(WorkFrame frame, string key);
    void Scroll(WorkFrame frame, int ticks);
}

public sealed partial class WindowsDesktop
{
    private static readonly HashSet<string> WorkExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComputerNeighbour", "conhost", "OpenConsole", "WindowsTerminal", "cmd", "powershell", "pwsh",
        "wt", "regedit", "Taskmgr", "consent", "CredentialUIBroker", "LogonUI", "explorer",
        "WindowsPowerShell", "wscript", "cscript", "mshta", "rundll32"
    };

    internal static bool SupportsWorkKey(string key) => key.ToUpperInvariant() is
        "ENTER" or "TAB" or "SHIFT+TAB" or "ESC" or "BACKSPACE" or "SPACE" or
        "UP" or "DOWN" or "LEFT" or "RIGHT" or "HOME" or "END" or "PAGEUP" or "PAGEDOWN" or "CTRL+A" or "CTRL+Z";

    internal static bool IsWorkApplication(string process, string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return process.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            name.Equals("calc", StringComparison.OrdinalIgnoreCase) && process.Equals("CalculatorApp", StringComparison.OrdinalIgnoreCase);
    }

    WorkWindow IWorkDesktop.CurrentWindow() => CurrentWorkWindow();

    WorkFrame IWorkDesktop.Observe(WorkWindow window)
    {
        if (CurrentWorkWindow() != window) throw new InvalidOperationException("The foreground window changed before inspection.");
        var image = CapturePng(window.Left, window.Top, window.Width, window.Height);
        if (CurrentWorkWindow() != window) throw new InvalidOperationException("The foreground window changed during inspection.");
        return new(window, image, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4)));
    }

    void IWorkDesktop.Focus(string executable)
    {
        EnsureWindows();
        var name = Path.GetFileNameWithoutExtension(executable);
        if (WorkExcludedProcesses.Contains(name)) throw new UnauthorizedAccessException("This application cannot be a desktop input target.");
        var names = name.Equals("calc", StringComparison.OrdinalIgnoreCase) ? new[] { name, "CalculatorApp" } : new[] { name };
        var windows = new HashSet<nint>();
        foreach (var candidate in names.SelectMany(Process.GetProcessesByName))
        {
            using (candidate)
            {
                var handle = candidate.MainWindowHandle;
                if (handle != 0) windows.Add(handle);
            }
        }
        if (windows.Count != 1) throw new InvalidOperationException("Bring the intended application window to the foreground. There must be exactly one open window for automatic focusing.");
        Activate(windows.Single());
        _ = CurrentWorkWindow();
    }

    void IWorkDesktop.Restore(WorkWindow window)
    {
        if (GetWindowThreadProcessId(window.Handle, out var pid) == 0 || pid != window.ProcessId)
            throw new InvalidOperationException("The approved window is no longer available.");
        Activate(window.Handle);
        if (CurrentWorkWindow() != window) throw new InvalidOperationException("The approved window changed. Inspect it again before sending input.");
    }

    void IWorkDesktop.Click(WorkFrame frame, int x, int y)
    {
        CheckWorkWindow(frame);
        var target = frame.ScreenPoint(x, y);
        var currentImage = CapturePng(frame.Window.Left, frame.Window.Top, frame.Window.Width, frame.Window.Height);
        if (!currentImage.AsSpan().SequenceEqual(frame.Image))
            throw new InvalidOperationException("The window's appearance changed. Inspect it again before clicking.");
        CheckWorkPoint(frame, target);
        if (!SetCursorPos(target.X, target.Y)) throw new Win32Exception();
        CheckWorkPoint(frame, target);
        Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } },
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0004 } } }]);
    }

    void IWorkDesktop.Type(WorkFrame frame, string text)
    {
        CheckWorkWindow(frame);
        var inputs = text.SelectMany(ch => new[] { KeyInput(0, ch, 0x0004), KeyInput(0, ch, 0x0004 | 0x0002) }).ToArray();
        CheckWorkWindow(frame);
        Send(inputs);
    }

    void IWorkDesktop.Key(WorkFrame frame, string key)
    {
        if (!SupportsWorkKey(key)) throw new ArgumentException("This shortcut is unavailable for desktop work.");
        CheckWorkWindow(frame);
        PressKey(key);
    }

    void IWorkDesktop.Scroll(WorkFrame frame, int ticks)
    {
        if (ticks is 0 or < -8 or > 8) throw new ArgumentOutOfRangeException(nameof(ticks), "Use 1 to 8 wheel ticks in either direction.");
        var center = frame.ScreenPoint(frame.Width / 2, frame.Height / 2);
        CheckWorkPoint(frame, center);
        if (!SetCursorPos(center.X, center.Y)) throw new Win32Exception();
        CheckWorkPoint(frame, center);
        Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0800, MouseData = unchecked((uint)(ticks * 120)) } } }]);
    }

    private void CheckWorkWindow(WorkFrame frame)
    {
        if (CurrentWorkWindow() != frame.Window)
            throw new InvalidOperationException("The target window changed. Inspect it again before sending input.");
    }

    private void CheckWorkPoint(WorkFrame frame, (int X, int Y) point)
    {
        CheckWorkWindow(frame);
        var hit = WindowFromPoint(new(point.X, point.Y));
        if (hit == 0 || GetAncestor(hit, 2) != frame.Window.Handle)
            throw new InvalidOperationException("Another window covers the requested position.");
    }

    private WorkWindow CurrentWorkWindow()
    {
        if (!IsInteractiveDesktop()) throw new InvalidOperationException("No interactive Windows desktop is available.");
        var window = GetForegroundWindow();
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        if (IsExcluded(title.ToString())) throw new UnauthorizedAccessException("The foreground window is excluded from desktop work.");
        var thread = GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (WorkExcludedProcesses.Contains(process.ProcessName))
            throw new UnauthorizedAccessException("Bring the application you want to work in to the foreground.");
        var origin = new WorkPoint(0, 0);
        if (!GetClientRect(window, out var rect) || !ClientToScreen(window, ref origin))
            throw new Win32Exception("Unable to locate the foreground window.");
        var left = Math.Max(0, origin.X);
        var top = Math.Max(0, origin.Y);
        var width = Math.Min(GetSystemMetrics(0), origin.X + rect.Right) - left;
        var height = Math.Min(GetSystemMetrics(1), origin.Y + rect.Bottom) - top;
        if (width < 1 || height < 1) throw new InvalidOperationException("The window must be visible on the primary monitor.");
        var owner = GetAncestor(window, 3);
        var gui = new WorkGuiInfo { Size = (uint)Marshal.SizeOf<WorkGuiInfo>() };
        var focus = GetGUIThreadInfo(thread, ref gui) ? gui.Focus : 0;
        return new(window, owner == 0 ? window : owner, pid, process.ProcessName, title.ToString(), left, top, width, height, focus);
    }

    private static void Activate(nint window)
    {
        if (GetForegroundWindow() == window) return;
        if (!IsInteractiveDesktop()) throw new InvalidOperationException("No interactive Windows desktop is available.");
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        if (!SetForegroundWindow(window) || GetForegroundWindow() != window)
            throw new InvalidOperationException("Windows could not focus the intended window. Switch to it yourself and inspect it again.");
    }

    [StructLayout(LayoutKind.Sequential)] private record struct WorkPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct WorkRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WorkGuiInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public WorkRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out WorkRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref WorkPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(WorkPoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref WorkGuiInfo info);
}
