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
    public PersonalProject Project { get; init; } = new();
    public LearningChallenge? Challenge { get; init; }
    public int Deferrals { get; init; }
}

internal sealed record PersonalProject
{
    public string Title { get; init; } = "";
    public string Aim { get; init; } = "";
    public string[] Outline { get; init; } = [];
    public string[] Scenes { get; init; } = [];
    public string Draft { get; init; } = "";
    public string Prompt { get; init; } = "";
    public PersonalReview? Review { get; init; }
    public string Destination { get; init; } = "";
    public string OriginalHash { get; init; } = "";
    public int Revisions { get; init; }
    public int Visit { get; init; }
    public bool TrainingPending { get; init; }
}

internal sealed record PersonalReview(string Decision, string Feedback, int Scene);
internal sealed record DeferredGoal(PersonalGoalState Goal, DateTimeOffset Due, string Reason);
internal sealed record DiaryEntry(DateTimeOffset At, PersonalGoal Goal, string Topic, string Observation, string Reference = "", string? Title = null);
internal sealed record LearningChallenge(string Tool, string? Target, DateTimeOffset At);
internal sealed record PersonalNotice(DateTimeOffset At, string Text);

internal sealed record PersonalLifeState
{
    internal const int QueueLimit = 16;
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
    public DeferredGoal[] Backlog { get; init; } = [];
    public PersonalGoal[] RecentProjects { get; init; } = [];
    public DateTimeOffset NextTitleAt { get; init; }

    public PersonalLifeState Remember(DiaryEntry note) => this with { Journal = [.. Journal.TakeLast(59), note] };
    public PersonalLifeState Schedule(PersonalGoalState goal, DateTimeOffset due, string reason) =>
        this with { Backlog = [.. Backlog.Where(item => item.Goal.Id != goal.Id), new(goal, due, reason)] };
    public PersonalLifeState Started(PersonalGoal goal) =>
        goal is PersonalGoal.Rest or PersonalGoal.NameEntry ? this :
        this with { RecentProjects = [.. RecentProjects.TakeLast(7), goal] };
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
            if (new FileInfo(file).Length > 4_000_000) throw new InvalidDataException("Personal life data exceeds its limit.");
            await using var input = File.OpenRead(file);
            var saved = await JsonSerializer.DeserializeAsync<PersonalLifeState>(input, AppConfig.Json, ct);
            if (saved is null || !Valid(saved.Active) || !Valid(saved.Suspended) ||
                saved.Previous is { } previous && !Enum.IsDefined(previous) ||
                saved.Challenge is { } challenge && (challenge.Tool is not { Length: > 0 and <= 40 } || challenge.Target is { Length: > 1200 }) ||
                saved.Notice is { } notice && notice.Text is not { Length: > 0 and <= 2400 } ||
                saved.Journal is null || saved.Journal.Length > 60 || saved.Journal.Any(note => note is null ||
                    !Enum.IsDefined(note.Goal) || note.Topic is null || note.Observation is null || note.Reference is null) ||
                saved.Backlog is not { Length: <= PersonalLifeState.QueueLimit } ||
                saved.Backlog.Any(item => item is null || item.Goal is null || !Valid(item.Goal) || item.Reason is not { Length: <= 300 }) ||
                saved.RecentProjects is not { Length: <= 8 } || !saved.RecentProjects.All(Enum.IsDefined) ||
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
        goal.Id != Guid.Empty && goal.Topic is { Length: <= 120 } && goal.Material is { Length: <= 14000 } &&
        goal.Reference is { Length: <= 1200 } && goal.Links is { Length: <= 3 } && goal.Links.All(link => link is { Length: <= 1200 }) &&
        goal.Failed is { Length: <= 32 } && goal.Failed.All(Enum.IsDefined) && goal.Deferrals is >= 0 and <= 100 &&
        (goal.Challenge is null || goal.Challenge.Tool is { Length: > 0 and <= 40 } && goal.Challenge.Target is not { Length: > 1200 }) &&
        goal.Project is { Title.Length: <= 100, Aim.Length: <= 1000, Draft.Length: <= 9000, Prompt.Length: <= 600,
            Destination.Length: <= 240, OriginalHash.Length: <= 64, Revisions: >= 0 and <= 8, Visit: >= 0 and <= 2,
            Outline.Length: <= 3, Scenes.Length: <= 3 } project &&
        project.Outline.All(item => item is { Length: <= 650 }) && project.Scenes.All(item => item is { Length: <= 3500 }) &&
        (project.Review is null || project.Review.Decision is ("accept" or "revise" or "research" or "defer" or "keep") &&
            project.Review.Feedback is { Length: <= 550 } && project.Review.Scene is >= 0 and <= 3);
}
