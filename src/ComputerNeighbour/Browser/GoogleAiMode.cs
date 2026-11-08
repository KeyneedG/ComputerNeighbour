using System.Diagnostics;
using System.Text.RegularExpressions;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Browser;

internal sealed record GoogleVisualResult(string Text, IReadOnlyList<WebCitation> Sources);

internal interface IGoogleVisualSearch
{
    Task<GoogleVisualResult> SearchAsync(string question, Action<string> status, CancellationToken ct);
}

/// <summary>Operates a dedicated Chrome window through Windows accessibility and keyboard input.</summary>
internal sealed class GoogleAiMode(WindowsDesktop desktop, string temporaryDirectory) : IGoogleVisualSearch
{
    private const int Button = 50000, Edit = 50004, Link = 50005, MenuItem = 50011;
    private const int Name = 30005, HasFocus = 30008, Enabled = 30010, AutomationId = 30011;
    private static readonly string[] AddFiles = ["Add files and tools", "Добавить файлы и инструменты"];
    private static readonly string[] Upload = ["Add images", "Upload images", "Upload image", "Upload files", "Добавить изображения", "Загрузить изображение"];
    private static readonly string[] Ask = ["Ask anything", "Спросите что угодно", "Задайте вопрос"];
    private static readonly string[] Submit = ["Send", "Submit", "Send message", "Отправить"];
    private static readonly string[] Stop = ["Stop", "Stop generating", "Stop response", "Остановить"];
    private int busy;

    public async Task<GoogleVisualResult> SearchAsync(string question, Action<string> status, CancellationToken ct)
    {
        status("Point at one corner of the image and press F8. Repeat at the opposite corner to send that area to Google. Esc cancels.");
        var image = await desktop.SelectScreenRegionAsync(ct);
        return await RunInBrowserAsync(image, question, status, null, null, ct);
    }

    internal Task<GoogleVisualResult> AskAsync(string question, string profile, Func<bool> desktopIdle, CancellationToken ct) =>
        RunInBrowserAsync(null, question, _ => { }, profile, desktopIdle, ct);

    private async Task<GoogleVisualResult> RunInBrowserAsync(byte[]? image, string question, Action<string> status,
        string? profile, Func<bool>? desktopIdle, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0)
            throw new InvalidOperationException("The previous Google browser operation is still finishing.");
        var background = false;
        string? file = null;
        try
        {
            if (image is not null)
            {
                Directory.CreateDirectory(temporaryDirectory);
                file = Path.Combine(temporaryDirectory, $"evelyn-visual-{Guid.NewGuid():N}.png");
                await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(image, ct);
            }
            if (desktopIdle?.Invoke() == false) throw new InvalidOperationException("The desktop is active again.");
            status("Opening Google AI Mode in a new Chrome window.");
            var browser = await desktop.OpenGoogleAiWindowAsync(ct, profile);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(110));
            var token = deadline.Token;
            var attachment = file;
            var operation = Task.Run(() =>
            {
                try { return Run(browser, attachment, question, status, desktopIdle, token); }
                finally
                {
                    if (profile is not null) desktop.ClosePersonalGoogleWindow(browser);
                    Finish(attachment);
                }
            }, CancellationToken.None);
            background = true;
            _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { return await operation.WaitAsync(token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Chrome did not finish the Google AI Mode search in time."); }
        }
        finally
        {
            if (!background) Finish(file);
        }
    }

    private void Finish(string? file)
    {
        try { if (file is not null) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref busy, 0); }
    }

    private GoogleVisualResult Run(WorkWindow browser, string? file, string question, Action<string> status,
        Func<bool>? desktopIdle, CancellationToken ct)
    {
        using var uia = new NativeUia();
        using var root = uia.ElementFromHandle(browser.Handle);
        if (file is not null) AttachImage(uia, root, browser, file, ct);

        using var composer = WaitForControl(uia, root, browser, [Edit], Ask, ct);
        VerifyTarget(uia, root, browser, ct);
        if (desktopIdle?.Invoke() == false) throw new InvalidOperationException("The desktop is active again.");
        Focus(uia, composer, browser, browser, ct);
        VerifyTarget(uia, root, browser, ct);
        if (desktopIdle?.Invoke() == false || uia.GetBoolean(composer, HasFocus) != true)
            throw new InvalidOperationException("Google's question field lost focus or the desktop became active.");
        desktop.TypeGoogleText(browser, browser, question);
        WaitFor(() => string.Equals(Normalize(uia.GetString(composer, NativeUia.ValueProperty) ??
            uia.ReadDocumentText(composer, 1000, ct) ?? ""), Normalize(question), StringComparison.Ordinal)
            ? browser : null, "Google's question field did not show the entered question.", 3, ct);
        var inputStamp = desktopIdle is null ? null : WindowsDesktop.GoogleInputStamp();
        using var send = WaitForControl(uia, root, browser, [Button], Submit, ct);
        using var page = CurrentDocument(uia, root, browser) ?? throw new InvalidOperationException("Google AI Mode is no longer visible.");
        var priorLinks = ReadLinks(uia, page, ct).Select(source => source.Url).ToHashSet(StringComparer.Ordinal);
        if (desktopIdle is null)
            Invoke(uia, root, browser, send, ct);
        else
        {
            VerifyTarget(uia, root, browser, ct);
            if (inputStamp is null || WindowsDesktop.GoogleInputStamp() != inputStamp || !uia.TryInvoke(send))
                throw new InvalidOperationException("Google's send control could not be used on the idle desktop.");
        }
        status("Image attached. Waiting for Google's reply.");
        return WaitForAnswer(uia, root, browser, question, priorLinks, file is not null, inputStamp, ct);
    }

    private void AttachImage(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser, string file, CancellationToken ct)
    {
        using (var attach = WaitForControl(uia, root, browser, [Button], AddFiles, ct))
            Invoke(uia, root, browser, attach, ct);
        using (var upload = WaitForControl(uia, root, browser, [MenuItem, Button, Link], Upload, ct))
            Invoke(uia, root, browser, upload, ct);

        var dialog = WaitFor(() => desktop.GoogleBrowserHasFocus(browser) ? null : desktop.RequireGoogleForeground(browser, true),
            "Chrome did not open its image file picker.", 8, ct);
        using (var dialogRoot = uia.ElementFromHandle(dialog.Handle))
        using (var fileName = Find(uia, dialogRoot, [Edit], item => uia.GetString(item, AutomationId) == "1148" ||
                   MatchName(uia.GetString(item, Name), ["File name", "File name:", "Имя файла", "Имя файла:"]))
                   ?? throw new InvalidOperationException("Chrome's file picker did not expose its filename field."))
        {
            Focus(uia, fileName, dialog, browser, ct);
            desktop.PressKey("CTRL+A");
            desktop.TypeGoogleText(browser, dialog, file);
            WaitFor(() => string.Equals(uia.GetString(fileName, NativeUia.ValueProperty), file, StringComparison.OrdinalIgnoreCase)
                ? dialog : null, "Chrome's file picker did not show the selected image path.", 4, ct);
            ct.ThrowIfCancellationRequested();
            desktop.RequireGoogleForeground(browser, true);
            if (uia.GetBoolean(fileName, HasFocus) != true) throw new InvalidOperationException("The file picker lost focus.");
            desktop.PressKey("ENTER");
        }

        WaitFor(() => desktop.GoogleBrowserHasFocus(browser) ? browser : null, "Chrome did not return from its image file picker.", 8, ct);
        var fileNameOnly = Path.GetFileName(file);
        using (var attachment = WaitFor(() =>
        {
            using var document = CurrentDocument(uia, root, browser);
            return document is null ? null : Find(uia, document, [Button, 50006], element =>
                (uia.GetString(element, Name) ?? "").Contains(fileNameOnly, StringComparison.OrdinalIgnoreCase));
        }, "Google did not expose a verified image attachment.", 16, ct))
        { }

    }

    private GoogleVisualResult WaitForAnswer(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser,
        string question, HashSet<string> priorLinks, bool visual, uint? inputStamp, CancellationToken ct)
    {
        string? previous = null;
        var stable = Stopwatch.StartNew();
        return WaitFor(() =>
        {
            if (inputStamp is not null && WindowsDesktop.GoogleInputStamp() != inputStamp)
                throw new InvalidOperationException("The desktop is active again.");
            using var document = CurrentDocument(uia, root, browser);
            if (document is null) return null;
            using var stopping = Find(uia, document, [Button], item => MatchName(uia.GetString(item, Name), Stop));
            var answer = stopping is null ? ExtractAnswer(ReadText(uia, document, ct), question, visual) : null;
            if (answer is null || answer != previous)
            {
                previous = answer;
                stable.Restart();
                return null;
            }
            if (stable.Elapsed < TimeSpan.FromSeconds(3)) return null;
            VerifyTarget(uia, root, browser, ct);
            var sources = ReadLinks(uia, document, ct).Where(source => !priorLinks.Contains(source.Url)).Take(8).ToArray();
            return new GoogleVisualResult(answer, sources);
        }, "Google AI Mode did not expose a completed answer for the submitted question.", 70, ct);
    }

    internal static string? ExtractAnswer(string text, string question, bool visual = true)
    {
        var lines = text.Replace("\uFFFC", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var normalizedQuestion = Normalize(question);
        var headings = visual ? new[] { "AI Mode reply for 1 image", "Ответ в режиме ИИ для 1 изображения" } :
            ["AI Mode reply for", "AI Mode response for", "Ответ в режиме ИИ"];
        var start = Array.FindLastIndex(lines, line =>
            headings.Any(heading => line.StartsWith(heading, StringComparison.OrdinalIgnoreCase)) &&
            Normalize(line).Contains(normalizedQuestion, StringComparison.Ordinal));
        if (start < 0 && !visual)
            start = Array.FindLastIndex(lines, line => Normalize(line).Equals(normalizedQuestion, StringComparison.Ordinal));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, line => MatchName(line,
            ["Good response", "Bad response", "Хороший ответ", "Плохой ответ"]));
        return end > start + 1 ? string.Join("\n", lines[(start + 1)..end]) : null;
    }

    private static IReadOnlyList<WebCitation> ReadLinks(NativeUia uia, NativeUia.ComPointer document, CancellationToken ct)
    {
        using var links = uia.FindByControlType(document, Link);
        var sources = new List<WebCitation>();
        for (var i = 0; i < Math.Min(links.Count, 160); i++)
        {
            ct.ThrowIfCancellationRequested();
            using var link = links.Get(i);
            var value = uia.GetString(link, NativeUia.ValueProperty);
            if (value is null || !WebParsing.IsWebUrl(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var url) || IsGoogleNavigation(url)) continue;
            var title = uia.GetString(link, Name);
            if (!string.IsNullOrWhiteSpace(title) && sources.All(source => source.Url != value))
                sources.Add(new(title, value, DateTimeOffset.UtcNow, "browser"));
        }
        return sources;
    }

    private static string ReadText(NativeUia uia, NativeUia.ComPointer document, CancellationToken ct) =>
        uia.ReadDocumentText(document, 16000, ct) is { Length: > 0 } text ? text :
            uia.ReadAccessibleText(document, 16000, ct, out _);

    private NativeUia.ComPointer WaitForControl(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser,
        int[] types, string[] names, CancellationToken ct) =>
        WaitFor(() =>
        {
            using var document = CurrentDocument(uia, root, browser);
            return document is null ? null : Find(uia, document, types, element => MatchName(uia.GetString(element, Name), names));
        }, $"Google AI Mode did not expose the “{names[0]}” control. Check whether AI Mode is available in Chrome.", 12, ct);

    private NativeUia.ComPointer? CurrentDocument(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser)
    {
        desktop.RequireGoogleForeground(browser);
        return Find(uia, root, [NativeUia.DocumentControlType], document =>
            IsAiModeUrl(uia.GetString(document, NativeUia.ValueProperty)), requireEnabled: false);
    }

    private void VerifyTarget(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = CurrentDocument(uia, root, browser);
        if (document is null || !IsAiModeUrl(uia.ReadAddress(root, ct)))
            throw new InvalidOperationException("Chrome's current page is not verified as Google AI Mode.");
        ct.ThrowIfCancellationRequested();
        desktop.RequireGoogleForeground(browser);
    }

    private void Invoke(NativeUia uia, NativeUia.ComPointer root, WorkWindow browser, NativeUia.ComPointer target, CancellationToken ct)
    {
        VerifyTarget(uia, root, browser, ct);
        if (uia.TryInvoke(target)) return;
        try { uia.SetFocus(target); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (uia.GetBoolean(target, HasFocus) == true)
        {
            VerifyTarget(uia, root, browser, ct);
            desktop.PressKey("ENTER");
            return;
        }
        var point = uia.ElementCenter(target) ??
            throw new InvalidOperationException("Chrome did not expose the position of the expected Google control.");
        VerifyTarget(uia, root, browser, ct);
        desktop.ClickGoogleControl(browser, point.X, point.Y);
    }

    private void Focus(NativeUia uia, NativeUia.ComPointer target, WorkWindow window, WorkWindow browser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = window.Handle != browser.Handle;
        if (desktop.RequireGoogleForeground(browser, dialog).Handle != window.Handle)
            throw new InvalidOperationException("The intended input window changed.");
        uia.SetFocus(target);
        WaitFor(() => desktop.RequireGoogleForeground(browser, dialog).Handle == window.Handle &&
            uia.GetBoolean(target, HasFocus) == true ? window : null, "Chrome did not focus the expected input control.", 3, ct);
    }

    private static NativeUia.ComPointer? Find(NativeUia uia, NativeUia.ComPointer root, int[] types,
        Func<NativeUia.ComPointer, bool> match, bool requireEnabled = true)
    {
        foreach (var type in types)
        {
            using var elements = uia.FindByControlType(root, type);
            for (var index = 0; index < Math.Min(elements.Count, 250); index++)
            {
                var element = elements.Get(index);
                var found = false;
                try
                {
                    found = uia.GetBoolean(element, NativeUia.IsOffscreenProperty) != true &&
                        (!requireEnabled || uia.GetBoolean(element, Enabled) != false) && match(element);
                    if (found) return element;
                }
                finally { if (!found) element.Dispose(); }
            }
        }
        return null;
    }

    private static T WaitFor<T>(Func<T?> check, string error, int seconds, CancellationToken ct) where T : class
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            ct.ThrowIfCancellationRequested();
            if (check() is { } value) return value;
            if (ct.WaitHandle.WaitOne(250)) ct.ThrowIfCancellationRequested();
        }
        throw new TimeoutException(error);
    }

    internal static bool IsAiModeUrl(string? value) =>
        Uri.TryCreate(NativeUia.WebAddress(value), UriKind.Absolute, out var url) && url.Scheme == "https" &&
        url.IsDefaultPort && url.UserInfo.Length == 0 && url.Host is ("www.google.com" or "google.com") &&
        (url.AbsolutePath == "/ai" || url.AbsolutePath == "/search" &&
            url.Query.TrimStart('?').Split('&').Contains("udm=50", StringComparer.Ordinal));

    private static bool IsGoogleNavigation(Uri url) =>
        url.Host is ("google.com" or "www.google.com" or "accounts.google.com" or "support.google.com" or "policies.google.com");

    private static bool MatchName(string? value, string[] names) =>
        value is not null && names.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
