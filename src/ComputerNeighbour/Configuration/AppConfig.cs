using System.Text.Json;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Internet;

namespace ComputerNeighbour.Configuration;

public sealed class AppConfig
{
    public string CharacterFile { get; set; } = "characters/neighbour.md";
    public string CharacterName { get; set; } = "Evelyn";
    public string UserName { get; set; } = "User";
    public ModelOptions Model { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public PlatformOptions Desktop { get; set; } = new();
    public ObservationOptions Observation { get; set; } = new();
    public ProactiveOptions Proactive { get; set; } = new();
    public NeedsOptions Needs { get; set; } = new();
    public PermissionOptions Permissions { get; set; } = new();
    public WeatherOptions Weather { get; set; } = new();
    public BrowserOptions Browser { get; set; } = new();
    public InternetOptions Internet { get; set; } = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static AppConfig Load(PortablePaths paths)
    {
        var file = paths.Inside("config/appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file)) File.WriteAllText(file, JsonSerializer.Serialize(new AppConfig(), Json));
        var result = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Empty configuration.");
        result.Validate(paths);
        return result;
    }

    public void Validate(PortablePaths paths)
    {
        if (Model is null || Memory is null || Audio is null || Desktop is null || Observation is null || Proactive is null || Needs is null || Permissions is null || Weather is null || Browser is null || Internet is null)
            throw new InvalidDataException("Configuration sections cannot be null.");
        if (string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length>80 || string.IsNullOrWhiteSpace(UserName) || UserName.Length>80)
            throw new InvalidDataException("Character/user names must contain 1–80 characters.");
        if (!Uri.TryCreate(Model.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !endpoint.IsLoopback)
            throw new InvalidDataException("Model endpoint must be local HTTP (localhost/127.0.0.1).");
        if (Model.ContextSize < 4096 || Model.MaxReplyTokens < 64 || Model.MaxReplyTokens > Model.ContextSize / 2 || Model.RequestTimeoutSeconds < 5)
            throw new InvalidDataException("Invalid context size, reply token limit or request timeout.");
        if (Proactive.MinIntervalSeconds < 10 || Proactive.MaxIntervalSeconds < Proactive.MinIntervalSeconds || Proactive.MaxIntervalSeconds > 86400 || Proactive.CooldownSeconds < 5 || Proactive.CheckSeconds < 2 || Proactive.QuietStartHour is <0 or >23 || Proactive.QuietEndHour is <0 or >23 || Proactive.MaxUnansweredMessages < 0)
            throw new InvalidDataException("Invalid proactive intervals.");
        if(Needs.BoredomMinutes is <1 or >1440||Needs.LonelinessMinutes is <1 or >1440||Needs.InvitationPatienceMinutes is <1 or >1440||
            Needs.RecentInputSeconds is <10 or >300||Needs.SketchIdleSeconds is <60 or >86400||Needs.SketchCooldownMinutes is <5 or >1440)
            throw new InvalidDataException("Invalid needs or sketch intervals.");
        if (Observation.ScreenIntervalSeconds < 10 || Audio.MaxRecordingSeconds < 1 || Audio.MaxRecordingSeconds > 300)
            throw new InvalidDataException("Invalid observation / audio interval.");
        if (Weather.Latitude is < -90 or > 90 || Weather.Longitude is < -180 or > 180 || Weather.RefreshMinutes < 10)
            throw new InvalidDataException("Invalid weather location / interval.");
        if (Permissions.ReadableDirectories is null || Permissions.WritableDirectories is null || Permissions.AllowedApplications is null || Desktop.ExcludedWindowTitles is null || Browser.AllowedProcesses is null)
            throw new InvalidDataException("Permission lists cannot be null.");
        if (Audio.SampleRate!=16000 || Audio.PushToTalkVirtualKey is <1 or >254 || Audio.InputDeviceId < -1 || Audio.OutputDeviceId < -1 || !double.IsFinite(Audio.SpeechRate) || Audio.SpeechRate is <0.5 or >2 || !double.IsFinite(Audio.SpeakerMatchThreshold))
            throw new InvalidDataException("Invalid audio device, sample rate, hotkey, speed or speaker threshold.");
        if (Model.Threads<1 || !double.IsFinite(Model.Temperature) || Model.Temperature is <0 or >2 || Model.StartupTimeoutSeconds<5)
            throw new InvalidDataException("Invalid model threads, temperature or startup timeout.");
        foreach (var directory in Permissions.ReadableDirectories) Tools.TextFileReader.ReadableRoot(paths,directory);
        foreach (var directory in Permissions.WritableDirectories) paths.Inside(directory);
        if(Permissions.MaxWritableFileBytes is <1024 or >10_000_000 || Browser.MaxCharacters is <1000 or >100_000 || Browser.ReadTimeoutSeconds is <1 or >60)
            throw new InvalidDataException("Invalid file size or browser read limits.");
        if(Internet.SearchProvider is not ("auto" or "duckduckgo" or "bing-rss")||Internet.MaxResults is <1 or >10||Internet.MaxArticles is <1 or >3||Internet.MaxArticleCharacters is <1000 or >50000||Internet.RequestTimeoutSeconds is <3 or >60||Internet.NewsMaxAgeDays is <1 or >365||Internet.TechnologyNewsFeeds is null||Internet.TechnologyNewsFeeds.Any(url=>!WebParsing.IsWebUrl(url)))
            throw new InvalidDataException("Invalid internet/search configuration.");
        foreach (var path in new[] { CharacterFile, Model.Executable, Model.ModelFile, Model.ProjectorFile, Audio.WhisperExecutable, Audio.WhisperModel, Audio.PiperExecutable, Audio.PiperModel })
            paths.Inside(path);
    }
}

public sealed class ModelOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:18389";
    public bool AutoStart { get; set; } = true;
    public string Executable { get; set; } = "runtimes/llama/llama-server.exe";
    public string ModelFile { get; set; } = "models/llm/qwen35-4b/Qwen3.5-4B-Q4_K_M.gguf";
    public string ProjectorFile { get; set; } = "models/llm/qwen35-4b/mmproj-F16.gguf";
    public int ContextSize { get; set; } = 16384;
    public int GpuLayers { get; set; } = 99;
    public int Threads { get; set; } = 6;
    public int MaxReplyTokens { get; set; } = 900;
    public double Temperature { get; set; } = 0.75;
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int StartupTimeoutSeconds { get; set; } = 180;
}
public sealed class ObservationOptions
{
    public bool IncludeScreenInUserTurns { get; set; } = false;
    public int ScreenIntervalSeconds { get; set; } = 180;
    public bool KeepScreenshots { get; set; } = false;
}
public sealed class ProactiveOptions
{
    public bool Enabled { get; set; } = true;
    public bool RequireFirstUserMessage { get; set; } = true;
    public int CheckSeconds { get; set; } = 5;
    public int MinIntervalSeconds { get; set; } = 240;
    public int MaxIntervalSeconds { get; set; } = 720;
    public int CooldownSeconds { get; set; } = 150;
    public int QuietStartHour { get; set; } = 0;
    public int QuietEndHour { get; set; } = 8;
    public bool TriggerOnWindowChange { get; set; } = false;
    public bool TriggerOnReturnFromIdle { get; set; } = true;
    public int AwayAfterSeconds { get; set; } = 300;
    public int MaxUnansweredMessages { get; set; } = 2;
}
public sealed class NeedsOptions
{
    public bool Enabled { get; set; } = true;
    public int BoredomMinutes { get; set; } = 20;
    public int LonelinessMinutes { get; set; } = 30;
    public bool InviteWhenLonely { get; set; } = true;
    public int RecentInputSeconds { get; set; } = 45;
    public int InvitationPatienceMinutes { get; set; } = 12;
    public bool DrawWhenBored { get; set; }
    public bool OpenPaint { get; set; } = true;
    public int SketchIdleSeconds { get; set; } = 480;
    public int SketchCooldownMinutes { get; set; } = 60;
}
public sealed class PermissionOptions
{
    public bool Microphone { get; set; } = true;
    public bool ScreenCapture { get; set; } = true;
    public bool ForegroundWindow { get; set; } = true;
    public bool SystemStatus { get; set; } = true;
    public bool ReadWeb { get; set; } = true;
    public bool SearchWeb { get; set; } = true;
    public bool ReadFiles { get; set; } = true;
    public bool ReadOpenDocuments { get; set; } = true;
    public bool WriteFiles { get; set; } = true;
    public bool ReadBrowser { get; set; } = true;
    public bool AllowAutonomousFileActions { get; set; } = true;
    public bool FullFileAccess { get; set; } = false;
    public bool ConfirmFileChanges { get; set; } = true;
    public int MaxWritableFileBytes { get; set; } = 1_000_000;
    public bool OpenApplications { get; set; } = true;
    public bool DesktopInput { get; set; } = false;
    public bool ConfirmActions { get; set; } = true;
    public bool WeatherNetwork { get; set; } = false;
    public List<string> ReadableDirectories { get; set; } = ["documents"];
    public List<string> WritableDirectories { get; set; } = ["documents"];
    public Dictionary<string,string> AllowedApplications { get; set; } = new() { ["notepad"] = "notepad.exe", ["calculator"] = "calc.exe" };
}
public sealed class WeatherOptions
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int RefreshMinutes { get; set; } = 30;
}
