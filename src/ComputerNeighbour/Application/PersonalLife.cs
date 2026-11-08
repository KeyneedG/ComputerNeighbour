using System.Text.Json;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Application;

internal sealed record PersonalGoalState(PersonalGoal Goal, Guid Id, DateTimeOffset Started, string Topic)
{
    public PersonalFact Facts { get; init; }
    public PersonalAction[] Failed { get; init; } = [];
    public string[] Links { get; init; } = [];
    public string Material { get; init; } = "";
    public string Reference { get; init; } = "";
    public DateTimeOffset? WakeAt { get; init; }
}

internal sealed record DiaryEntry(DateTimeOffset At, PersonalGoal Goal, string Topic, string Observation, string Reference = "");
internal sealed record LearningChallenge(string Tool, string? Target, DateTimeOffset At);
internal sealed record PersonalNotice(DateTimeOffset At, string Text);

internal sealed record PersonalLifeState
{
    public PersonalGoalState? Active { get; init; }
    public PersonalGoalState? Suspended { get; init; }
    public PersonalGoal? Previous { get; init; }
    public bool Paused { get; init; }
    public DateTimeOffset NextStep { get; init; }
    public DateTimeOffset DeferredUntil { get; init; }
    public DateTimeOffset GoogleAfter { get; init; }
    public DateTimeOffset ResearchAfter { get; init; }
    public DateTimeOffset TrainingAfter { get; init; }
    public DateTimeOffset LastNotice { get; init; }
    public LearningChallenge? Challenge { get; init; }
    public PersonalNotice? Notice { get; init; }
    public DiaryEntry[] Journal { get; init; } = [];
    public DialogueExample[] Exercises { get; init; } = [];

    public PersonalLifeState Remember(DiaryEntry note) => this with { Journal = [.. Journal.TakeLast(59), note] };
}

// The event loop is the sole writer. Replace the checkpoint only after the new snapshot is complete.
internal sealed class PersonalLife(PortablePaths paths)
{
    private const string Name = "data/autonomy/life.json";
    private bool loaded;
    internal PersonalLifeState State { get; private set; } = new();

    internal async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        var file = paths.Inside(Name);
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 600_000) throw new InvalidDataException("Personal life data exceeds its limit.");
            await using var input = File.OpenRead(file);
            var saved = await JsonSerializer.DeserializeAsync<PersonalLifeState>(input, AppConfig.Json, ct);
            if (saved is null || !Valid(saved.Active) || !Valid(saved.Suspended) ||
                saved.Previous is { } previous && !Enum.IsDefined(previous) ||
                saved.Challenge is { } challenge && (challenge.Tool is not { Length: > 0 and <= 40 } || challenge.Target is { Length: > 1200 }) ||
                saved.Notice is { } notice && notice.Text is not { Length: > 0 and <= 2400 } ||
                saved.Journal is null || saved.Journal.Length > 60 || saved.Journal.Any(note => note is null ||
                    !Enum.IsDefined(note.Goal) || note.Topic is null || note.Observation is null || note.Reference is null) ||
                saved.Exercises is null || saved.Exercises.Length > 16 || saved.Exercises.Any(example => example is null ||
                    string.IsNullOrWhiteSpace(example.Prompt) || string.IsNullOrWhiteSpace(example.Reply)))
                throw new InvalidDataException("Invalid personal life checkpoint.");
            State = saved;
        }
        loaded = true;
    }

    internal async Task SaveAsync(PersonalLifeState next, CancellationToken ct)
    {
        var file = paths.Inside(Name);
        var temporary = paths.Inside(Name + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, next, AppConfig.Json, ct);
            File.Move(temporary, file, overwrite: true);
            State = next;
            loaded = true;
        }
        finally { File.Delete(temporary); }
    }

    internal void Clear()
    {
        foreach (var path in new[] { paths.Inside(Name), paths.Inside(Name + ".tmp") })
            if (File.Exists(path)) File.Delete(path);
        State = new() { Paused = State.Paused };
        loaded = true;
    }

    private static bool Valid(PersonalGoalState? goal) => goal is null || Enum.IsDefined(goal.Goal) &&
        goal.Id != Guid.Empty && goal.Topic is { Length: <= 120 } && goal.Material is { Length: <= 8000 } &&
        goal.Reference is { Length: <= 1200 } && goal.Links is { Length: <= 3 } && goal.Links.All(link => link is { Length: <= 1200 }) &&
        goal.Failed is { Length: <= 12 } && goal.Failed.All(Enum.IsDefined);
}
