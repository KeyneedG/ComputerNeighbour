using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ComputerNeighbour.Platform;

public sealed partial class WindowsDesktop
{
    internal async Task<byte[]> SelectScreenRegionAsync(CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        var first = await ReadCornerAsync(elapsed, ct);
        var second = await ReadCornerAsync(elapsed, ct);
        if (first.Window with { FocusedControl = 0 } != second.Window with { FocusedControl = 0 })
            throw new InvalidOperationException("Both corners must be in the same unchanged foreground window.");
        var window = second.Window;
        var left = Math.Min(first.Point.X, second.Point.X);
        var top = Math.Min(first.Point.Y, second.Point.Y);
        var right = Math.Max(first.Point.X, second.Point.X);
        var bottom = Math.Max(first.Point.Y, second.Point.Y);
        if (right - left < 32 || bottom - top < 32 || left < window.Left || top < window.Top ||
            right >= window.Left + window.Width || bottom >= window.Top + window.Height)
            throw new InvalidOperationException("Select two corners of an area inside the foreground window.");
        foreach (var point in new WorkPoint[] { new(left, top), new(right, top), new(left, bottom), new(right, bottom),
            new(left + (right - left) / 2, top + (bottom - top) / 2) })
            if (GetAncestor(WindowFromPoint(point), 2) != window.Handle)
                throw new InvalidOperationException("Another window covers part of the selection.");
        ct.ThrowIfCancellationRequested();
        if (CurrentWorkWindow() != window) throw new InvalidOperationException("The selected window changed before capture.");
        var image = CapturePng(left, top, right - left + 1, bottom - top + 1);
        if (CurrentWorkWindow() != window) throw new InvalidOperationException("The selected window changed during capture.");
        return image;
    }

    private async Task<(WorkWindow Window, WorkPoint Point)> ReadCornerAsync(Stopwatch elapsed, CancellationToken ct)
    {
        var released = false;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();
            if (IsKeyDown(0x1B)) throw new InvalidOperationException("Screen selection was cancelled.");
            var down = IsKeyDown(0x77); // F8
            if (released && down)
            {
                var window = CurrentWorkWindow();
                if (!GetCursorPos(out var point)) throw new InvalidOperationException("Windows did not return the cursor position.");
                return (window, point);
            }
            released |= !down;
            await Task.Delay(25, ct);
        }
        throw new TimeoutException("The screen selection expired.");
    }

    internal async Task<WorkWindow> OpenGoogleAiWindowAsync(CancellationToken ct, string? personalProfile = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Google visual search requires Windows.");
        if (!IsInteractiveDesktop()) throw new InvalidOperationException("An interactive Windows desktop is required.");
        var original = new HashSet<nint>();
        EnumGoogleWindows((handle, _) => { original.Add(handle); return true; }, 0);
        var chrome = new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        }.FirstOrDefault(path => path is not null && Path.IsPathFullyQualified(path) &&
            Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path));
        if (chrome is null) throw new InvalidOperationException("Google Chrome was not found.");
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        if (personalProfile is not null)
        {
            start.ArgumentList.Add("--user-data-dir=" + personalProfile);
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-background-mode");
        }
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add("https://www.google.com/ai");
        ct.ThrowIfCancellationRequested();
        using var launched = Process.Start(start) ?? throw new IOException("Could not start Chrome.");
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
            {
                ct.ThrowIfCancellationRequested();
                var active = GetForegroundWindow();
                if (active != 0 && !original.Contains(active) && GoogleWindowClass(active) == "Chrome_WidgetWin_1" &&
                    CurrentWorkWindow() is { Process: "chrome" } window && window.Handle == active &&
                    (personalProfile is null || window.ProcessId == launched.Id))
                    return window;
                await Task.Delay(150, ct);
            }
            throw new TimeoutException("Chrome did not open a new foreground window for Google AI Mode.");
        }
        catch when (personalProfile is not null)
        {
            try { if (!launched.HasExited) launched.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
    }

    internal void ClosePersonalGoogleWindow(WorkWindow browser)
    {
        if (GetWindowThreadProcessId(browser.Handle, out var pid) != 0 && pid == browser.ProcessId &&
            GoogleWindowClass(browser.Handle) == "Chrome_WidgetWin_1")
            PostMessage(browser.Handle, 0x0010, 0, 0); // WM_CLOSE for this dedicated window only.
    }

    internal static uint? GoogleInputStamp()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }

    internal WorkWindow RequireGoogleForeground(WorkWindow browser, bool fileDialog = false)
    {
        var active = CurrentWorkWindow();
        var matches = fileDialog
            ? active.Owner == browser.Handle && GoogleWindowClass(active.Handle) == "#32770" &&
              active.Process.Equals(browser.Process, StringComparison.OrdinalIgnoreCase)
            : active.ProcessId == browser.ProcessId && active.Handle == browser.Handle;
        if (!matches) throw new InvalidOperationException("Focus left the dedicated Google window.");
        return active;
    }

    internal bool GoogleBrowserHasFocus(WorkWindow browser) => GetForegroundWindow() == browser.Handle;

    internal void TypeGoogleText(WorkWindow browser, WorkWindow target, string text)
    {
        var inputs = text.SelectMany(character => new[]
        {
            KeyInput(0, character, 0x0004),
            KeyInput(0, character, 0x0004 | 0x0002)
        }).ToArray();
        if (RequireGoogleForeground(browser, target.Handle != browser.Handle).Handle != target.Handle)
            throw new InvalidOperationException("The Google input control's window changed.");
        Send(inputs);
    }

    internal void ClickGoogleControl(WorkWindow browser, int x, int y)
    {
        var active = RequireGoogleForeground(browser);
        if (x < active.Left || y < active.Top || x >= active.Left + active.Width || y >= active.Top + active.Height ||
            GetAncestor(WindowFromPoint(new(x, y)), 2) != active.Handle)
            throw new InvalidOperationException("The Google control is outside its visible Chrome window.");
        Click(x, y);
    }

    private static string GoogleWindowClass(nint handle)
    {
        var name = new StringBuilder(64);
        GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }

    private delegate bool GoogleEnumWindow(nint handle, nint parameter);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")] private static extern bool EnumGoogleWindows(GoogleEnumWindow callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WorkPoint point);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
}
