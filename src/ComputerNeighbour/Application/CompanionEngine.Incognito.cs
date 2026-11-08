using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private const string IncognitoAccessMessage = "Computer access is read-only in incognito.";
    internal bool IsIncognito { get; private init; }
    internal string MemorySummary => memory.Summary;
    internal string DescribeCharacter() => emotions.DescribeGrowth();
    private string SessionMemoryContext => IsIncognito
        ? "This is a temporary incognito session. The conversation, profile, tasks and relationship use an in-memory copy of their earlier state. Changes here last until incognito ends. Computer access is read-only."
        : "Conversation, profile and relationship persist locally across restarts.";

    internal string DescribeStatus() =>
        $"Root: {paths.Root}\nSession: {(IsIncognito ? "incognito (temporary)" : "regular")}\n{emotions.Status}\n{NeedsStatus}\n{AccessStatus}\n" +
        (model is LocalModel local ? $"Deep analysis: {(!IsIncognito && local.DeepReady ? local.DeepModelName + " available" : "regular model")}\n" : "") +
        $"Memory: {memory.StoredCharacters} recent characters; summary {memory.Summary.Length}; paused={Paused}; mute={Muted}.";

    internal async Task<CompanionEngine> ForkIncognitoAsync(CancellationToken ct)
    {
        await InitializeProfileAsync(ct);
        var currentRelationship = relationship;
        await currentRelationship.InitializeAsync(ct);
        var sources = await sourceHistory.ForkInMemoryAsync(ct);
        var notes = await thoughtJournal.ForkInMemoryAsync(ct);
        var isolatedConfig = JsonSerializer.SerializeToElement(config, AppConfig.Json).Deserialize<AppConfig>(AppConfig.Json)!;
        isolatedConfig.Permissions.Microphone = false;
        isolatedConfig.Permissions.WriteFiles = false;
        isolatedConfig.Permissions.AllowAutonomousFileActions = false;
        isolatedConfig.Permissions.OpenApplications = false;
        isolatedConfig.Permissions.DesktopInput = false;
        isolatedConfig.Permissions.SearchWeb = false;
        isolatedConfig.Permissions.ReadWeb = false;
        isolatedConfig.Permissions.WeatherNetwork = false;
        isolatedConfig.Browser.PublicUrlFallback = false;
        isolatedConfig.Observation.KeepScreenshots = false;
        isolatedConfig.Proactive.Enabled = false;

        var isolatedWeather = new WeatherService(isolatedConfig);
        try
        {
            return new CompanionEngine(paths, isolatedConfig, model, memory.ForkInMemory(),
                emotions.ForkInMemory(needs.Levels), new WindowsDesktop(isolatedConfig.Desktop), audio, ui, isolatedWeather,
                openDocuments:openDocuments, captureScreen:captureScreen)
            {
                IsIncognito = true,
                profile = profile.ForkInMemory(),
                profileReady = true,
                relationshipStore = currentRelationship.ForkInMemory(relationshipName),
                relationshipName = relationshipName,
                sourceHistory = sources,
                thoughtJournal = notes,
                hasUserInteracted = hasUserInteracted,
                LastInteraction = LastInteraction,
                UnansweredMessages = UnansweredMessages,
                Paused = Paused,
                Muted = Muted,
                lastScreen = lastScreen,
                lastScreenHash = lastScreenHash,
                lastScreenObservation = lastScreenObservation,
                lastResearch = lastResearch,
                pendingTaskChoice = pendingTaskChoice,
                lastTurnWasWorkTask = lastTurnWasWorkTask
            };
        }
        catch
        {
            isolatedWeather.Dispose();
            throw;
        }
    }

    internal async Task WaitForAudioIdleAsync(CancellationToken ct)
    {
        InterruptSpeech();
        await speech.WaitAsync(ct);
        await audio.WaitForInputIdleAsync(ct);
    }

    internal void SetModelIncognito(bool active)
    {
        if (model is LocalModel local) local.SetIncognito(active);
    }
}
