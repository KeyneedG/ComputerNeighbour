using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class EngineTests
{
    private static string Decision(string reply = "", List<ToolRequest>? actions = null, bool speak = true, string memory = "") =>
        JsonSerializer.Serialize(new CompanionDecision { Speak = speak, Reply = reply, Memory = memory, Emotion = new EmotionDelta(), Actions = actions ?? [] }, AppConfig.Json);
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("explicit read requests are grounded in actual article text before the first model call", async root =>
        {
            var model = new ScriptedModel(
                "The article explains portable solar charging.");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "SOLAR_ARTICLE_SENTINEL: portable panels charge a battery during daylight.");
            await fixture.Engine.RespondAsync("Прочитай documents/article.md и расскажи о чём статья.", "alex", false, false, CancellationToken.None);
            Check.Equal(1, model.Calls.Count);
            Check.True(model.Calls[0].Messages.Any(m => m.Text.Contains("SOURCE:") && m.Text.Contains("SOLAR_ARTICLE_SENTINEL")), "The first model call did not receive the actual article result.");
            Check.True(fixture.Output.Lines.Any(s => s.EndsWith("The article explains portable solar charging.")), "The final English reply was not printed.");
            Check.True(fixture.Memory.Recent.Any(e => e.Role == "assistant" && e.Text == "The article explains portable solar charging."), "The final reply was not persisted.");
            Check.True(fixture.Memory.Recent.Any(e => e.Role == "user" && e.SpeakerId == "alex"), "The speaker identity was lost in the engine.");
            Check.True(model.Calls.All(c => !c.Json && c.Messages.All(m => m.Image is null)), "A direct source-summary turn captured an image or requested structured dialogue output.");
        });
        await suite.Case("model-initiated reads still support a real file tool round trip for requests outside the explicit router", async root =>
        {
            var model = new ScriptedModel(
                Decision(actions: [new("read_file", Args(new { path = "documents/article.md" }))]),
                Decision("The article explains portable solar charging."));
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "MODEL_READ_SENTINEL: solar panels charge a battery.");
            await fixture.Engine.RespondAsync("Would you inspect documents/article.md for me?", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls[1].Messages.Any(m => m.Text.Contains("TOOL RESULTS") && m.Text.Contains("MODEL_READ_SENTINEL")), "The model-initiated tool result was lost.");
            Check.True(fixture.Output.Lines.Any(l => l.EndsWith("The article explains portable solar charging.")), "The tool-assisted answer was not printed.");
        });
        await suite.Case("direct plaintext summaries retry Russian output before printing and saving English facts", async root =>
        {
            var model = new ScriptedModel("В статье рассказано о зарядке аккумулятора солнечной панелью.", "A solar panel charges the battery during daylight.");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "Солнечная панель заряжает аккумулятор днём.");
            await fixture.Engine.RespondAsync("Прочитай documents/article.md.", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls.All(c => !c.Json), "A plaintext summary retry switched into structured dialogue mode.");
            Check.True(fixture.Output.Lines.Any(l => l.EndsWith("A solar panel charges the battery during daylight.")), "The English plaintext retry was not printed.");
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("В статье рассказано")), "The rejected Russian summary was printed.");
            Check.True(fixture.Memory.Recent.Where(e => e.Role is "assistant" or "note").All(e => !e.Text.Contains("В статье рассказано")), "The rejected Russian summary was persisted.");
        });
        await suite.Case("two empty plaintext summaries fail without committing an assistant reply or factual note", async root =>
        {
            var model = new ScriptedModel("", " \n\t");
            await using var fixture = await Fixture.CreateAsync(root, model);
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "A readable source whose failed summary must not be fabricated.");
            await Check.ThrowsAsync<InvalidDataException>(() => fixture.Engine.RespondAsync("Read documents/article.md.", "user", false, false, CancellationToken.None));
            Check.Equal(2, model.Calls.Count);
            Check.True(model.Calls.All(c => !c.Json), "The direct summary unexpectedly requested structured output.");
            Check.Equal(0, fixture.Output.Lines.Count);
            Check.True(fixture.Memory.Recent.All(e => e.Role == "user"), "A failed summary committed an assistant reply or a factual note.");
            Check.Equal("", fixture.Memory.Summary);
        });
        await suite.Case("autonomous model decisions cannot execute requested tools or generate approval prompts", async root =>
        {
            var model = new ScriptedModel(Decision(actions: [new("read_file", Args(new { path = "documents/should-not-be-read.md" })), new("open_app", Args(new { name = "never" }))], speak: false));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("Scheduled opportunity.", "environment", true, false, CancellationToken.None);
            Check.Equal(1, model.Calls.Count);
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("/approve") || l.Contains("[Tool")), "An autonomous decision attempted a computer action.");
            Check.Equal(0, fixture.Memory.Recent.Count);
            Check.True(!model.Calls.SelectMany(c => c.Messages).Any(m => m.Text.Contains("TOOL RESULTS")), "An autonomous tool result reached the model.");
        });
        await suite.Case("explicit application requests are queued honestly without calling the model or starting an executable", async root =>
        {
            var model = new ScriptedModel();
            await using var fixture = await Fixture.CreateAsync(root, model);
            fixture.Config.Permissions.AllowedApplications["never"] = "documents/this-executable-does-not-exist.exe";
            await fixture.Engine.RespondAsync("Open never.", "user", false, false, CancellationToken.None);
            Check.Equal(0, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("[Approval 1]") && l.Contains("/approve 1")), "The user was not given an explicit approval request.");
            Check.True(fixture.Output.Lines.Any(l => l.Contains("I've prepared that action. Please approve request 1")), "The pending action was not described honestly.");
            fixture.Engine.Reject(1);
            Check.True(fixture.Output.Lines.Contains("Action rejected."), "The pending action could not be rejected.");
        });
        await suite.Case("model-initiated application requests still require approval for wording outside the explicit router", async root =>
        {
            var model = new ScriptedModel(
                Decision(actions: [new("open_app", Args(new { name = "never" }))]),
                Decision("Please approve the pending application request."));
            await using var fixture = await Fixture.CreateAsync(root, model);
            fixture.Config.Permissions.AllowedApplications["never"] = "documents/this-executable-does-not-exist.exe";
            await fixture.Engine.RespondAsync("I would like the never application running.", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("[Approval 1]") && l.Contains("/approve 1")), "The model-initiated action bypassed approval.");
            Check.True(model.Calls[1].Messages.Any(m => m.Text.Contains("TOOL RESULTS") && m.Text.Contains("Pending approval 1; not executed.")), "The model did not receive an honest pending result.");
        });
        await suite.Case("malformed and incomplete decision JSON cannot authorize tools or persist fabricated facts", async root =>
        {
            var model = new ScriptedModel(
                "{\"speak\":true,\"reply\":\"\",\"memory\":\"fabricated\",\"emotion\":{},\"actions\":[{\"tool\":\"open_app\",\"args\":{\"name\":\"notepad\"}}]",
                "{\"reply\":\"Incomplete schema\",\"actions\":[{\"tool\":\"open_app\",\"args\":{\"name\":\"notepad\"}}]}");
            await using var fixture = await Fixture.CreateAsync(root, model);
            var rejected = false;
            try { await fixture.Engine.RespondAsync("Hello.", "user", false, false, CancellationToken.None); }
            catch (Exception e) when (e is JsonException or InvalidDataException) { rejected = true; }
            Check.True(rejected, "An incomplete or malformed decision was accepted.");
            Check.Equal(2, model.Calls.Count);
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("/approve") || l.Contains("[Tool")), "An invalid decision caused an action.");
            Check.True(fixture.Memory.Recent.All(e => e.Role == "user"), "An invalid decision persisted model-generated memory or a reply.");
        });
        await suite.Case("Russian model output is retried before an English response is printed", async root =>
        {
            var model = new ScriptedModel(Decision("Это ответ на русском языке."), Decision("I understand Russian and will answer in English."));
            await using var fixture = await Fixture.CreateAsync(root, model);
            await fixture.Engine.RespondAsync("Ты понимаешь русский?", "user", false, false, CancellationToken.None);
            Check.Equal(2, model.Calls.Count);
            Check.True(fixture.Output.Lines.Any(l => l.Contains("I understand Russian and will answer in English.")), "The valid English retry was not printed.");
            Check.True(!fixture.Output.Lines.Any(l => l.Contains("Это ответ")), "Rejected Russian output was printed.");
        });
    }

    private sealed class ScriptedModel(params string[] responses) : IChatModel
    {
        public List<ModelCall> Calls { get; } = [];
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new(messages.ToArray(), json));
            if (Calls.Count > responses.Length) throw new InvalidOperationException("The engine requested an unexpected extra model response.");
            return Task.FromResult(responses[Calls.Count - 1]);
        }
    }
    private sealed record ModelCall(ChatMessage[] Messages, bool Json);
    private sealed class CapturingOutput : ICompanionOutput
    {
        public List<string> Lines { get; } = [];
        public void Write(string text) => Lines.Add(text);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required AppConfig Config { get; init; }
        public required MemoryStore Memory { get; init; }
        public required CompanionEngine Engine { get; init; }
        public required AudioService Audio { get; init; }
        public required WeatherService Weather { get; init; }
        public required CapturingOutput Output { get; init; }
        public static async Task<Fixture> CreateAsync(string root, IChatModel model)
        {
            var paths = new PortablePaths(root);
            var config = new AppConfig();
            config.Audio.Enabled = false;
            config.Permissions.Microphone = false;
            config.Permissions.ScreenCapture = false;
            config.Permissions.ForegroundWindow = false;
            config.Permissions.WeatherNetwork = false;
            Directory.CreateDirectory(paths.Inside("characters"));
            await File.WriteAllTextAsync(paths.Inside(config.CharacterFile), "You are a calm, curious fictional neighbour. Respond in natural English.");
            var memory = new MemoryStore(root, config.Memory); await memory.InitializeAsync();
            var emotion = new EmotionEngine(root); await emotion.InitializeAsync();
            var audio = new AudioService(root, config.Audio);
            var weather = new WeatherService(config);
            var output = new CapturingOutput();
            return new Fixture
            {
                Config = config, Memory = memory, Audio = audio, Weather = weather, Output = output,
                Engine = new CompanionEngine(paths, config, model, memory, emotion, new WindowsDesktop(config.Desktop), audio, output, weather) { Paused = true }
            };
        }
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Audio.DisposeAsync();
            Weather.Dispose();
        }
    }
}
