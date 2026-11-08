using System.Net;
using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class BehaviorTests
{
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private static ToolRegistry BuiltIn(string root, AppConfig config) => ToolRegistry.BuiltIn(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop)));

    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("portable paths reject traversal, absolute paths, drive-relative and UNC paths", root =>
        {
            var paths = new PortablePaths(root);
            foreach (var path in new[] { "../escape.txt", "documents/../../escape.txt", "/escape.txt", "\\escape.txt", "C:\\escape.txt", "C:escape.txt", "\\\\server\\share\\secret.txt", "", " " })
                Check.Throws<InvalidDataException>(() => paths.Inside(path));
            Check.Equal(Path.Combine(root, "documents", "article.md"), paths.Inside("documents/article.md"));
            Check.Equal(Path.Combine(root, "documents", "article.md"), paths.Inside("documents/nested/../article.md"));
        });
        await suite.Case("model endpoint, model paths and invalid scheduling config are rejected", root =>
        {
            var paths = new PortablePaths(root);
            new AppConfig().Validate(paths);
            var external = new AppConfig(); external.Model.Endpoint = "https://example.com";
            Check.Throws<InvalidDataException>(() => external.Validate(paths));
            var escaped = new AppConfig(); escaped.Model.ModelFile = "../model.gguf";
            Check.Throws<InvalidDataException>(() => escaped.Validate(paths));
            var intervals = new AppConfig(); intervals.Proactive.MaxIntervalSeconds = 1;
            Check.Throws<InvalidDataException>(() => intervals.Validate(paths));
            var quiet = new AppConfig(); quiet.Proactive.QuietStartHour = -1;
            Check.Throws<InvalidDataException>(() => quiet.Validate(paths));
        });
        await suite.Case("abliteration installation selects both checkpoints and supports reverting", root =>
        {
            var paths = new PortablePaths(root);
            var config = AppConfig.Load(paths);
            Check.Equal(config.Model.ModelFile, config.Model.EffectiveModelFile);

            var marker = paths.Inside(ModelOptions.AbliterationMarker);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
            var installed = AppConfig.Load(paths);
            Check.Equal(ModelOptions.AmadeusModelFile, installed.Model.EffectiveModelFile);
            Check.Equal(ModelOptions.AmadeusProjectorFile, installed.Model.EffectiveProjectorFile);

            var executable = paths.Inside("runtimes/ollama/ollama.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "");
            var manifest = paths.Inside("runtimes/ollama/models/manifests/registry.ollama.ai/huihui_ai/qwen3.8-abliterated/27b");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest, "");
            Check.True(OllamaDeepModel.IsInstalled(paths, installed.Model.UseAbliterated), "The abliterated specialist was not selected.");
            Check.True(!OllamaDeepModel.IsInstalled(paths, false), "The stock profile tried to use the abliterated specialist.");
            Check.Equal("huihui_ai/qwen3.8-abliterated:27b", OllamaDeepModel.ModelName(installed.Model.UseAbliterated));

            var configFile = paths.Inside("config/appsettings.json");
            config.Model.Abliterated = false;
            File.WriteAllText(configFile, JsonSerializer.Serialize(config, AppConfig.Json));
            var reverted = AppConfig.Load(paths).Model;
            Check.Equal(config.Model.ModelFile, reverted.EffectiveModelFile);
            Check.Equal(config.Model.ProjectorFile, reverted.EffectiveProjectorFile);
            Check.Equal("qwen3.8:27b", OllamaDeepModel.ModelName(reverted.UseAbliterated));
            config.Model.Abliterated = true;
            config.Model.AutoStart = false;
            Check.Throws<InvalidDataException>(() => config.Validate(paths));
        });
        await suite.Case("quiet hours support midnight crossing, daytime intervals and disabled intervals", _ =>
        {
            for (var hour = 0; hour < 24; hour++)
            {
                Check.Equal(hour >= 22 || hour < 7, ComputerNeighbour.Program.IsQuietHour(hour, 22, 7));
                Check.Equal(hour >= 9 && hour < 17, ComputerNeighbour.Program.IsQuietHour(hour, 9, 17));
                Check.Equal(false, ComputerNeighbour.Program.IsQuietHour(hour, 8, 8));
            }
        });
        await suite.Case("changing tools require explicit approval before their implementation runs", async root =>
        {
            var config = new AppConfig();
            var registry = new ToolRegistry(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop)));
            var tool = new CountingTool("mutate", true);
            registry.Register(tool);
            var request = new ToolRequest("MUTATE", Args(new { text = "literal value" }));
            Check.True(registry.RequiresApproval(request), "Changing tool bypassed the approval requirement.");
            var blocked = await registry.ExecuteAsync(request, false, CancellationToken.None);
            Check.True(!blocked.Success && tool.Executions == 0, "Unapproved implementation ran.");
            var approved = await registry.ExecuteAsync(request, true, CancellationToken.None);
            Check.True(approved.Success && tool.Executions == 1, "Explicitly approved tool did not execute once.");
        });
        await suite.Case("read-only extensions execute without approval and configured confirmation can be disabled", async root =>
        {
            var config = new AppConfig();
            var registry = new ToolRegistry(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop)));
            var read = new CountingTool("read", false); var change = new CountingTool("change", true);
            registry.Register(read); registry.Register(change);
            Check.True((await registry.ExecuteAsync(new("read"), false, CancellationToken.None)).Success, "Read-only extension was blocked.");
            config.Permissions.ConfirmActions = false;
            Check.True((await registry.ExecuteAsync(new("change"), false, CancellationToken.None)).Success, "Explicit configuration to disable confirmations was ignored.");
            Check.Equal(1, read.Executions); Check.Equal(1, change.Executions);
            Check.Throws<ArgumentException>(() => registry.Register(new CountingTool("read", false)));
        });
        await suite.Case("unknown tools fail closed and cancellation remains cancellation", async root =>
        {
            var config = new AppConfig();
            var registry = new ToolRegistry(new(new PortablePaths(root), config, new WindowsDesktop(config.Desktop)));
            Check.True(!(await registry.ExecuteAsync(new("invented_tool"), true, CancellationToken.None)).Success, "Unknown tool reported success.");
            registry.Register(new CancellationTool());
            await Check.ThrowsAsync<OperationCanceledException>(() => registry.ExecuteAsync(new("cancel"), true, CancellationToken.None));
        });
        await suite.Case("disabled web/file/application/input permissions block even approved requests", async root =>
        {
            var config = new AppConfig();
            config.Permissions.ReadWeb = false; config.Permissions.ReadFiles = false;
            config.Permissions.OpenApplications = false; config.Permissions.DesktopInput = false;
            var registry = BuiltIn(root, config);
            var requests = new[]
            {
                new ToolRequest("read_web", Args(new { url = "https://example.com" })),
                new ToolRequest("read_file", Args(new { path = "documents/article.md" })),
                new ToolRequest("open_app", Args(new { name = "notepad" })),
                new ToolRequest("click", Args(new { x = 1, y = 1 })),
                new ToolRequest("type_text", Args(new { text = "never sent" })),
                new ToolRequest("press_key", Args(new { key = "ENTER" }))
            };
            foreach (var request in requests)
            {
                var result = await registry.ExecuteAsync(request, true, CancellationToken.None);
                Check.True(!result.Success && result.Text.Contains("disabled", StringComparison.OrdinalIgnoreCase), "A disabled permission did not block " + request.Tool);
            }
        });
        await suite.Case("file reader restricts roots, file types, size and traversal while reading allowed UTF-8", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents"));
            Directory.CreateDirectory(Path.Combine(root, "documents-other"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "article.md"), "Текст статьи. Article text.");
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "binary.exe"), "not actually executable");
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "large.txt"), new string('x', 1_000_001));
            await File.WriteAllTextAsync(Path.Combine(root, "documents-other", "secret.txt"), "out of scope");
            var registry = BuiltIn(root, new AppConfig());
            var allowed = await registry.ExecuteAsync(new("read_file", Args(new { path = "documents/article.md" })), false, CancellationToken.None);
            Check.True(allowed.Success && allowed.Text.Contains("Текст статьи."), "Allowed UTF-8 article could not be read.");
            foreach (var path in new[] { "documents-other/secret.txt", "../secret.txt", "documents/binary.exe", "documents/large.txt" })
                Check.True(!(await registry.ExecuteAsync(new("read_file", Args(new { path })), false, CancellationToken.None)).Success, "Reader accepted an out-of-scope/unsupported file: " + path);
        });
        await suite.Case("application aliases reject arbitrary executable requests without launching anything", async root =>
        {
            var registry = BuiltIn(root, new AppConfig());
            var result = await registry.ExecuteAsync(new("open_app", Args(new { name = "powershell.exe -Command anything" })), true, CancellationToken.None);
            Check.True(!result.Success && result.Text.Contains("Unknown application alias"), "Application tool accepted arbitrary commands.");
        });
        await suite.Case("web filter rejects local/private/link-local/shared/multicast and IPv4-mapped private addresses", _ =>
        {
            foreach (var address in new[] { "0.0.0.0", "0.5.4.3", "10.1.2.3", "127.0.0.1", "169.254.169.254", "172.16.1.1", "172.31.255.255", "192.168.1.1", "100.64.0.1", "100.127.255.255", "198.18.1.1", "198.19.1.1", "224.0.0.1", "239.1.2.3", "255.255.255.255", "::", "::1", "fe80::1", "fec0::1", "fc00::1", "fd12:3456::1", "ff02::1", "::ffff:127.0.0.1", "::ffff:192.168.0.1" })
                Check.True(!PublicWeb.IsPublic(IPAddress.Parse(address)), "Nonpublic address was accepted: " + address);
            foreach (var address in new[] { "1.1.1.1", "8.8.8.8", "172.32.0.1", "2001:4860:4860::8888", "2606:4700:4700::1111", "::ffff:8.8.8.8" })
                Check.True(PublicWeb.IsPublic(IPAddress.Parse(address)), "Public address was rejected: " + address);
        });
        await suite.Case("web filter rejects reserved documentation and transition address space", _ =>
        {
            foreach (var address in new[] { "192.0.0.1", "192.0.2.1", "192.88.99.1", "198.51.100.1", "203.0.113.1", "2001:db8::1", "2002:7f00:1::1" })
                Check.True(!PublicWeb.IsPublic(IPAddress.Parse(address)), "Reserved/transition address was accepted: " + address);
        });
        await suite.Case("article reader rejects file URLs, embedded credentials and custom ports before network access", async _ =>
        {
            foreach (var url in new[] { "file:///C:/Windows/win.ini", "https://user:password@example.com/", "http://example.com:8000/", "ftp://example.com/article" })
                await Check.ThrowsAsync<HttpRequestException>(() => PublicWeb.ReadAsync(url, CancellationToken.None));
        });
    }

    private sealed class CountingTool(string name, bool changesComputer) : ICompanionTool
    {
        public string Name => name;
        public string Description => "Test extension.";
        public bool ChangesComputer => changesComputer;
        public int Executions { get; private set; }
        public Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, ToolContext context, CancellationToken ct)
        { Executions++; return Task.FromResult("Executed once."); }
    }
    private sealed class CancellationTool : ICompanionTool
    {
        public string Name => "cancel";
        public string Description => "Cancellation test.";
        public bool ChangesComputer => false;
        public Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, ToolContext context, CancellationToken ct) => throw new OperationCanceledException();
    }
}
