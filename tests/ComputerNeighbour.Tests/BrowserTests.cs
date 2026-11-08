using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class BrowserTests
{
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("current-browser requests pass actual accessible text and URL to the English summary model", async root =>
        {
            var browser = new FakeBrowser(new("synthetic-browser", "Test article", "https://example.org/solar", "BROWSER_TEXT_SENTINEL: solar panels charge a battery during daylight.", false));
            var model = new FakeModel("The page explains that solar panels charge batteries during daylight.");
            await using var fixture = await Fixture.CreateAsync(root, model, browser);
            await fixture.Engine.RespondAsync("Read the current browser page and summarize it.", "user", false, false, CancellationToken.None);
            Check.Equal(1, browser.Calls);
            Check.Equal(1, model.Calls.Count);
            Check.True(!model.Calls[0].Json, "Browser summaries should use the grounded English text path.");
            Check.True(model.Calls[0].Messages.Any(m => m.Text.Contains("BROWSER_TEXT_SENTINEL") && m.Text.Contains("https://example.org/solar")), "The model did not receive actual page text and source URL.");
            Check.True(fixture.Output.Lines.Any(l => l.Contains("The page explains that solar panels charge batteries during daylight.")&&l.Contains("https://example.org/solar")), "The grounded browser summary and verified URL were not printed.");
            Check.True(fixture.Memory.Recent.Any(e => e.Role == "assistant" && e.Text.Contains("The page explains that solar panels charge batteries during daylight.")&&e.Text.Contains("https://example.org/solar")), "The answer with its actual source was not saved.");
        });
        await suite.Case("browser permission and browser enabled setting both block the reader before invocation", async root =>
        {
            var config = new AppConfig();
            var browser = new FakeBrowser(new("synthetic", "Blocked article", null, "MUST_NOT_READ", false));
            var registry = ToolRegistry.BuiltIn(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop), browser));
            config.Permissions.ReadBrowser = false;
            var blocked = await registry.ExecuteAsync(new("read_browser"), true, CancellationToken.None);
            Check.True(!blocked.Success && blocked.Text.Contains("disabled", StringComparison.OrdinalIgnoreCase), "ReadBrowser permission did not block reading.");
            Check.Equal(0, browser.Calls);
            config.Permissions.ReadBrowser = true;
            config.Browser.Enabled = false;
            blocked = await registry.ExecuteAsync(new("read_browser"), false, CancellationToken.None);
            Check.True(!blocked.Success && browser.Calls == 0, "Disabled browser settings still invoked the reader.");
        });
        await suite.Case("browser read failures report the actual limitation without inventing a summary", async root =>
        {
            var browser = new FakeBrowser(new("synthetic", "No accessible text", null, "", false)) { Error = new InvalidOperationException("Accessible page text is unavailable.") };
            var model = new FakeModel();
            await using var fixture = await Fixture.CreateAsync(root, model, browser);
            await fixture.Engine.RespondAsync("Прочитай текущую страницу браузера.", "user", false, false, CancellationToken.None);
            Check.Equal(1, browser.Calls);
            Check.Equal(0, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("Accessible page text is unavailable.")), "The real browser failure was hidden.");
            Check.True(!fixture.Memory.Recent.Any(e => e.Role == "note"), "A failed browser read saved invented page facts.");
        });
        await suite.Case("browser tool labels truncated page data and propagates cancellation", async root =>
        {
            var config = new AppConfig();
            var browser = new FakeBrowser(new("synthetic", "Long article", "https://example.org/long", "ONLY_READ_PORTION", true));
            var registry = ToolRegistry.BuiltIn(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop), browser));
            var read = await registry.ExecuteAsync(new("read_browser"), false, CancellationToken.None);
            Check.True(read.Success && read.Text.Contains("ONLY_READ_PORTION") && read.Text.Contains("text limit"), "The tool did not disclose the incomplete source.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => registry.ExecuteAsync(new("read_browser"), false, cancelled.Token));
        });
        await suite.Case("English and Russian current-browser references route to actual browser reading", _ =>
        {
            var permissions = new PermissionOptions();
            foreach (var text in new[] { "Read the current browser page.", "Summarize this tab.", "Прочитай текущую страницу браузера.", "Можешь, пожалуйста, прочитать эту вкладку?" })
                Check.Equal("read_browser", IntentRouter.Resolve(text, permissions)?.Tool, "Current browser request was not grounded: " + text);
            Check.Equal("read_web", IntentRouter.Resolve("Read this page: https://example.org/article", permissions)?.Tool);
            Check.Equal("read_file", IntentRouter.Resolve("Read documents/article.md in the browser.", permissions)?.Tool);
            foreach (var text in new[] { "Don't read the browser.", "Не читай браузер.", "\"Read the current browser page\" is an example." })
                Check.True(IntentRouter.Resolve(text, permissions) is null, "Non-request language initiated browser reading.");
        });
        await suite.Case("native browser options reject invalid limits and command/path process entries without accessing Windows", _ =>
        {
            new WindowsBrowserReader(new(), new());
            new WindowsBrowserReader(new() { AllowedProcesses = ["chrome.exe", "msedge", "waterfox"] }, new());
            Check.Throws<ArgumentException>(() => new WindowsBrowserReader(new() { MaxCharacters = 99 }, new()));
            Check.Throws<ArgumentException>(() => new WindowsBrowserReader(new() { ReadTimeoutSeconds = 0 }, new()));
            foreach (var entry in new[] { "", "..", "chrome.exe --flag", "C:\\chrome.exe", "../chrome", "chrome.dll" })
                Check.Throws<ArgumentException>(() => new WindowsBrowserReader(new() { AllowedProcesses = [entry] }, new()));
        });
        await suite.Case("Google visual search uses the explicitly requested question and actual Chrome result", async root =>
        {
            const string source = "https://example.org/visual-source";
            var google = new FakeGoogle(new("RESULT_TEXT: The sample image is a linocut.", [new("Visual source", source, DateTimeOffset.UtcNow, "browser")]));
            var model = new FakeModel("Google identifies the sample as a linocut.");
            await using var fixture = await Fixture.CreateAsync(root, model, new FakeBrowser(new("", "", null, "", false)), google);
            await fixture.Engine.TryHandleAccessCommandAsync("/desktop on", default);
            await fixture.Engine.RespondAsync("/google Какая здесь техника печати?", "user", false, false, default);
            Check.True(google.Questions.SequenceEqual(["Какая здесь техника печати?"]), "The upload workflow did not use only the current question.");
            Check.True(model.Calls.Single().Messages.Last().Text.Contains("RESULT_TEXT"), "No observed result reached the answer model.");
            Check.True(fixture.Output.Lines.Last().Contains(source), "A visible source link was lost.");
        });
        await suite.Case("Google visual search requires current permission and the main user's request", async root =>
        {
            var google = new FakeGoogle(new("Unexpected result.", []));
            await using var fixture = await Fixture.CreateAsync(root, new FakeModel(), new FakeBrowser(new("", "", null, "", false)), google);
            await fixture.Engine.RespondAsync("/google What is this?", "user", false, false, default);
            Check.True(fixture.Output.Lines.Last().Contains("guided desktop access"), "The disabled desktop was not reported.");
            await fixture.Engine.TryHandleAccessCommandAsync("/desktop on", default);
            await fixture.Engine.RespondAsync("/google What is this?", "guest", false, false, default);
            await fixture.Engine.RespondAsync("/google What is this? Don't look at my screen.", "user", false, false, default);
            await fixture.Engine.RespondAsync("/google Определи автора, но не отправляй изображение.", "user", false, false, default);
            await fixture.Engine.TryHandleAccessCommandAsync("/ask before edit", default);
            await fixture.Engine.RespondAsync("/google What is this?", "user", false, false, default);
            Check.Equal(0, google.Questions.Count);
        });
        await suite.Case("Google visual input is pinned to the Google AI Mode origin and route", _ =>
        {
            foreach (var url in new[] { "https://www.google.com/ai", "google.com/search?udm=50&aep=11" })
                Check.True(GoogleAiMode.IsAiModeUrl(url), "Rejected Google AI Mode URL: " + url);
            foreach (var url in new[] { "https://www.google.com/search?udm=2", "https://google.com.example.org/search?udm=50",
                         "https://www.google.com@other.example/ai", "https://www.google.com:5555/ai", "http://www.google.com/ai",
                         "https://www.google.com/search?q=udm%3D50", "https://accounts.google.com/ai" })
                Check.True(!GoogleAiMode.IsAiModeUrl(url), "Accepted an unrelated browser target: " + url);
        });
        await suite.Case("Google reply extraction requires this question with its image and excludes interface text", _ =>
        {
            const string question = "What color is this?";
            const string header = "AI Mode reply for 1 image and " + question;
            var page = "Google Account: profile information\nYou sent: 1 image and said: " + question + "\n" +
                header + "\n\uFFFC\nThe rectangle is red.\nGood response\nBad response\nAsk anything";
            Check.Equal("The rectangle is red.", GoogleAiMode.ExtractAnswer(page, question));
            Check.True(GoogleAiMode.ExtractAnswer(page, "A different question?") is null, "An earlier reply was accepted.");
            Check.True(GoogleAiMode.ExtractAnswer(header + "\nStill generating", question) is null, "An incomplete reply was accepted.");
            Check.True(GoogleAiMode.ExtractAnswer(page.Replace("reply for 1 image and ", "reply for "), question) is null,
                "A text-only reply was accepted.");
        });
    }

    private sealed class FakeGoogle(GoogleVisualResult result) : IGoogleVisualSearch
    {
        public List<string> Questions { get; } = [];
        public Task<GoogleVisualResult> SearchAsync(string question, Action<string> status, CancellationToken ct)
        {
            Questions.Add(question);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBrowser(BrowserPage page) : IBrowserReader
    {
        public int Calls { get; private set; }
        public Exception? Error { get; init; }
        public Task<BrowserPage> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Error is null ? Task.FromResult(page) : Task.FromException<BrowserPage>(Error);
        }
    }
    private sealed record ModelCall(ChatMessage[] Messages, bool Json);
    private sealed class FakeModel(params string[] responses) : IChatModel
    {
        public List<ModelCall> Calls { get; } = [];
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(new(messages.ToArray(), json));
            if (Calls.Count > responses.Length) throw new InvalidOperationException("Unexpected model call during browser test.");
            return Task.FromResult(responses[Calls.Count - 1]);
        }
    }
    private sealed class Output : ICompanionOutput
    {
        public List<string> Lines { get; } = [];
        public void Write(string text) => Lines.Add(text);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required CompanionEngine Engine { get; init; }
        public required MemoryStore Memory { get; init; }
        public required AudioService Audio { get; init; }
        public required WeatherService Weather { get; init; }
        public required Output Output { get; init; }
        public static async Task<Fixture> CreateAsync(string root, IChatModel model, IBrowserReader browser, IGoogleVisualSearch? google = null)
        {
            var paths = new PortablePaths(root);
            var config = new AppConfig(); config.Audio.Enabled = false;
            config.Permissions.ScreenCapture = google is not null; config.Permissions.ForegroundWindow = false;
            config.Permissions.Microphone = false; config.Permissions.WeatherNetwork = false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile), "You are a calm English-speaking companion.");
            var memory = new MemoryStore(root, config.Memory); await memory.InitializeAsync();
            var emotions = new EmotionEngine(root); await emotions.InitializeAsync();
            var audio = new AudioService(root, config.Audio); var weather = new WeatherService(config); var output = new Output();
            return new() { Audio = audio, Weather = weather, Output = output, Memory = memory, Engine = new(paths, config, model, memory, emotions, new WindowsDesktop(config.Desktop), audio, output, weather, browser) { Paused = true, GoogleVisualSearchOverride = google } };
        }
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Audio.DisposeAsync(); Weather.Dispose(); }
    }
}
