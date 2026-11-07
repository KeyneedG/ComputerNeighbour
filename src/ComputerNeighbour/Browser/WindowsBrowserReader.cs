using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Browser;

/// <summary>
/// Reads the browser's existing accessibility document. It never navigates, types, opens a
/// debugging port, or accesses cookies. UIA provider calls run on a single-flight background MTA.
/// A hung third-party provider can retain at most one worker, not block the application's main loop.
/// </summary>
public sealed class WindowsBrowserReader : IBrowserReader
{
    private readonly BrowserOptions options;
    private readonly PlatformOptions privacy;
    private readonly bool allowPublicFetch;
    private readonly HashSet<string> allowedProcesses;
    private readonly object workerGate = new();
    private Task<BrowserPage>? inFlight;
    private WindowIdentity? lastBrowser;

    public WindowsBrowserReader(BrowserOptions options, PlatformOptions privacy,bool allowPublicFetch=false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(privacy);
        if (options.MaxCharacters is < 100 or > 100000 || options.ReadTimeoutSeconds is < 1 or > 60 || options.AllowedProcesses is null || privacy.ExcludedWindowTitles is null)
            throw new ArgumentException("Browser limits or process/privacy lists are invalid.");
        if (options.AllowedProcesses.Count > 64 || options.AllowedProcesses.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 80 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') || p.Contains("..", StringComparison.Ordinal) || p.Contains('.') && !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Browser allowedProcesses must contain process basenames, optionally ending in .exe; paths and command lines are not supported.");
        this.options = options;
        this.privacy = privacy;
        this.allowPublicFetch=allowPublicFetch;
        allowedProcesses = new(options.AllowedProcesses.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cheap HWND/PID tracking only. Call this only while browser observation is permitted.</summary>
    public void ObserveForeground()
    {
        if (!options.Enabled || !OperatingSystem.IsWindows()) return;
        var browser = GetBrowserWindow(GetForegroundWindow());
        if (browser is not null) Volatile.Write(ref lastBrowser, browser);
    }

    public async Task<BrowserPage> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled) throw new UnauthorizedAccessException("Browser reading is disabled.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Reading the open browser requires an interactive Windows desktop.");
        var selected = SelectWindow();
        EnsureNotExcluded(selected.Title);
        Task<BrowserPage> operation;
        lock (workerGate)
        {
            if (inFlight is { IsCompleted: false })
                throw new InvalidOperationException("The browser's previous accessibility request is still pending. Wait for it to finish, or restart ComputerNeighbour if that browser provider is unresponsive.");
            var completion = new TaskCompletionSource<BrowserPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlight = operation = completion.Task;
            // Observe failures even if the caller times out before a delayed provider returns.
            _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var worker = new Thread(() =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ReadTimeoutSeconds));
                try { completion.TrySetResult(ReadWindow(selected, timeout.Token)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(timeout.Token); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }) { IsBackground = true, Name = "ComputerNeighbour browser reader" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        try { return await operation.WaitAsync(TimeSpan.FromSeconds(options.ReadTimeoutSeconds), cancellationToken); }
        catch (TimeoutException) { throw new TimeoutException("The browser did not expose its page text within the configured timeout. Bring the intended tab to the foreground and retry; alternatively provide its public URL."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The browser accessibility read exceeded its configured time limit. No page content was returned."); }
    }

    private BrowserPage ReadWindow(WindowIdentity window, CancellationToken token)
    {
        EnsureWindowUnchanged(window);
        using var automation = new NativeUia();
        using var root = automation.ElementFromHandle(window.Handle);
        token.ThrowIfCancellationRequested();
        var initialUrl = automation.ReadAddress(root, token);
        using var documents = automation.FindByControlType(root, NativeUia.DocumentControlType);
        var count = Math.Min(documents.Count, 64);
        var text = "";
        var truncated = false;
        string? documentUrl = null;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var document = documents.Get(i);
            if (automation.GetBoolean(document, NativeUia.IsOffscreenProperty) == true || automation.GetBoolean(document, NativeUia.IsPasswordProperty) == true) continue;
            documentUrl ??= NativeUia.WebAddress(automation.GetString(document, NativeUia.ValueProperty));
            // TextPattern covers actual document text, including off-viewport text exposed by the provider.
            text = automation.ReadDocumentText(document, options.MaxCharacters + 1, token);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = automation.ReadAccessibleText(document, options.MaxCharacters + 1, token, out var accessibilityTruncated);
                truncated |= accessibilityTruncated;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                truncated |= text.Length > options.MaxCharacters;
                if (text.Length > options.MaxCharacters) text = text[..options.MaxCharacters];
                break; // Prefer the outermost visible document, not a different background tab/frame.
            }
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            var publicUrl=initialUrl??documentUrl;
            if(allowPublicFetch&&options.PublicUrlFallback&&publicUrl is not null&&WebParsing.IsWebUrl(publicUrl))
            {
                var fetched=PublicWeb.ReadDocumentAsync(publicUrl,token).GetAwaiter().GetResult();
                var page=WebParsing.Article(fetched,options.MaxCharacters);
                if(string.IsNullOrWhiteSpace(page.Text))throw new IOException("Neither browser accessibility nor the public page returned readable text.");
                if(System.Text.RegularExpressions.Regex.IsMatch(page.Title,@"^(?:just a moment|access denied|robot check|attention required)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("The public fallback returned an access check; it was not treated as an article.");
                var addressAfterFetch=automation.ReadAddress(root,token);
                if(initialUrl is not null&&addressAfterFetch is not null&&!string.Equals(initialUrl,addressAfterFetch,StringComparison.Ordinal))throw new InvalidOperationException("The browser navigated during public-page fallback. Retry on the intended tab.");
                EnsureWindowUnchanged(window);
                return new(window.ProcessName,window.Title,page.Url,page.Text,page.Truncated,"public-url-fallback");
            }
            throw new InvalidOperationException("The selected browser did not expose document text or an accessible public address. Keep its intended tab visible and retry; /read URL can fetch a public article directly. Window titles alone are not page contents.");
        }
        token.ThrowIfCancellationRequested();
        var finalUrl = automation.ReadAddress(root, token);
        EnsureWindowUnchanged(window);
        if (initialUrl is not null && finalUrl is not null && !string.Equals(initialUrl, finalUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("The browser navigated while its page was being read. Retry on the intended tab.");
        return new(window.ProcessName, window.Title, finalUrl ?? initialUrl ?? documentUrl, text.Trim(), truncated);
    }

    private WindowIdentity SelectWindow()
    {
        var active = GetBrowserWindow(GetForegroundWindow());
        if (active is not null) { Volatile.Write(ref lastBrowser, active); return active; }
        var remembered = Volatile.Read(ref lastBrowser);
        if (remembered is not null && GetBrowserWindow(remembered.Handle) is { } current && current.ProcessId == remembered.ProcessId)
            return current;
        if (remembered is not null && IsWindow(remembered.Handle))
        {
            GetWindowThreadProcessId(remembered.Handle, out var pid);
            if (pid == remembered.ProcessId && IsIconic(remembered.Handle))
                throw new InvalidOperationException("The remembered browser window is minimized. Restore its intended tab before reading; another browser window will not be substituted.");
        }
        var candidates = new List<WindowIdentity>();
        EnumWindows((handle, _) =>
        {
            if (GetWindow(handle, 4) == IntPtr.Zero && GetBrowserWindow(handle) is { } candidate) candidates.Add(candidate);
            return true;
        }, IntPtr.Zero);
        if (candidates.Count == 0) throw new InvalidOperationException("No visible supported browser window was found. Open the desired page in Chrome, Edge, Firefox, Brave, Opera or Vivaldi, then retry.");
        if (candidates.Count != 1) throw new InvalidOperationException("More than one browser window is open. Focus the intended browser window briefly, then return to the console and retry so the correct page can be selected.");
        Volatile.Write(ref lastBrowser, candidates[0]);
        return candidates[0];
    }

    private WindowIdentity? GetBrowserWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        handle = GetAncestor(handle, 2);
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle)) return null;
        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;
            if (!allowedProcesses.Contains(processName)) return null;
            var title = GetTitle(handle);
            return string.IsNullOrWhiteSpace(title) ? null : new(handle, pid, processName, title);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private void EnsureWindowUnchanged(WindowIdentity original)
    {
        var current = GetBrowserWindow(original.Handle);
        if (current is null || current.ProcessId != original.ProcessId || !string.Equals(current.Title, original.Title, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected browser window or page changed while reading. Focus the intended page and retry.");
        EnsureNotExcluded(current.Title);
    }

    private void EnsureNotExcluded(string title)
    {
        if (privacy.ExcludedWindowTitles.Any(value => !string.IsNullOrWhiteSpace(value) && title.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Browser reading is paused for this excluded/private window.");
    }
    private static string GetTitle(IntPtr handle) { var text = new StringBuilder(2048); GetWindowText(handle, text, text.Capacity); return text.ToString(); }
    private sealed record WindowIdentity(IntPtr Handle, uint ProcessId, string ProcessName, string Title);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
}
