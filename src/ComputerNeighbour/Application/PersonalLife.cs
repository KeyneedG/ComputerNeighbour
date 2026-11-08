using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Internet;

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
    public WebImageLink[] Images { get; init; } = [];
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
    public string DraftHash { get; init; } = "";
    public string ImageNote { get; init; } = "";
}

internal sealed record PersonalReview(string Decision, string Feedback, int Scene);
internal sealed record DeferredGoal(PersonalGoalState Goal, DateTimeOffset Due, string Reason);
internal sealed record DiaryEntry(DateTimeOffset At, PersonalGoal Goal, string Topic, string Observation, string Reference = "", string? Title = null);
internal sealed record LearningChallenge(string Tool, string? Target, DateTimeOffset At);
internal sealed record PersonalNotice(DateTimeOffset At, string Text, int Priority = 0);
internal enum ActivityKind { Writing, Research, Reading, Drawing, Practice, Organizing }
internal sealed record ActivityFocus(ActivityKind Kind, double Seconds);
internal sealed record ActivityBreak(ActivityKind Kind, DateTimeOffset Until);
internal sealed record PersonalWebEvent(DateTimeOffset At, string Step, string Topic, string Result, string Reference = "");
internal sealed record PersonalReflection(DateTimeOffset At, string Topic, string Stage, string Note, string Reference = "");
internal sealed record PersonalArtifact(string Path, string Title, DateTimeOffset? MissingSince = null, bool Reported = false);

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
    public ActivityFocus? Focus { get; init; }
    public ActivityBreak[] Breaks { get; init; } = [];
    public PersonalWebEvent[] WebHistory { get; init; } = [];
    public PersonalReflection[] Reflections { get; init; } = [];
    public PersonalArtifact[] Artifacts { get; init; } = [];
    public bool ArtifactsInitialized { get; init; }

    public PersonalLifeState Remember(DiaryEntry note) => this with { Journal = [.. Journal.TakeLast(59), note] };
    public PersonalLifeState Schedule(PersonalGoalState goal, DateTimeOffset due, string reason) =>
        this with { Backlog = [.. Backlog.Where(item => item.Goal.Id != goal.Id), new(goal, due, reason)] };
    public PersonalLifeState Started(PersonalGoal goal) =>
        goal is PersonalGoal.Rest or PersonalGoal.NameEntry ? this :
        this with { RecentProjects = [.. RecentProjects.TakeLast(7), goal] };
    public PersonalLifeState LogWeb(PersonalWebEvent item) => this with { WebHistory = [.. WebHistory.TakeLast(31), item] };
    public PersonalLifeState Think(PersonalReflection item) => this with { Reflections = [.. Reflections.TakeLast(59), item] };
    public PersonalLifeState Own(string path, string title) => this with
    {
        Artifacts = [.. Artifacts.Where(item => !item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).TakeLast(79), new(path, title)]
    };
    public PersonalLifeState Offer(PersonalNotice item) =>
        Notice is { } current && current.Priority > item.Priority && item.At - current.At < TimeSpan.FromDays(1)
            ? this : this with { Notice = item };
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
                saved.Focus is { } focus && (!Enum.IsDefined(focus.Kind) || !double.IsFinite(focus.Seconds) || focus.Seconds is < 0 or > 86400) ||
                saved.Breaks is not { Length: <= 6 } || saved.Breaks.Any(item => item is null || !Enum.IsDefined(item.Kind)) ||
                saved.WebHistory is not { Length: <= 32 } || saved.WebHistory.Any(item => item is null || item.Step is null ||
                    item.Topic is null || item.Result is null || item.Reference is null) ||
                saved.Reflections is not { Length: <= 60 } || saved.Reflections.Any(item => item is null || item.Topic is null ||
                    item.Stage is null || item.Note is null || item.Reference is null) ||
                saved.Artifacts is not { Length: <= 80 } || saved.Artifacts.Any(item => item is null ||
                    item.Path is not { Length: > 0 and <= 240 } || item.Title is not { Length: <= 120 }) ||
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
        goal.Images is { Length: <= 4 } && goal.Images.All(image => image is not null &&
            image.Url is { Length: <= 1200 } && image.Caption is { Length: <= 200 }) &&
        (goal.Challenge is null || goal.Challenge.Tool is { Length: > 0 and <= 40 } && goal.Challenge.Target is not { Length: > 1200 }) &&
        goal.Project is { Title.Length: <= 100, Aim.Length: <= 1000, Draft.Length: <= 9000, Prompt.Length: <= 600,
            Destination.Length: <= 240, OriginalHash.Length: <= 64, DraftHash.Length: <= 64, ImageNote.Length: <= 1000,
            Revisions: >= 0 and <= 8, Visit: >= 0 and <= 2,
            Outline.Length: <= 3, Scenes.Length: <= 3 } project &&
        project.Outline.All(item => item is { Length: <= 650 }) && project.Scenes.All(item => item is { Length: <= 3500 }) &&
        (project.Review is null || project.Review.Decision is ("accept" or "revise" or "research" or "defer" or "keep") &&
            project.Review.Feedback is { Length: <= 550 } && project.Review.Scene is >= 0 and <= 3);
}
