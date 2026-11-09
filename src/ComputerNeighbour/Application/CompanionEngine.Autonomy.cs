using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private sealed record PublicTopic(string Name, string Query, string Question);
    private static readonly PublicTopic[] Interests =
    [
        new("Cyanotypes", "cyanotype Anna Atkins process museum", "What makes the cyanotypes of Anna Atkins look so delicate?"),
        new("Night skies", "dark adaptation night sky rods astronomy", "Why are faint stars easier for people to see with averted vision?"),
        new("Bird dialects", "birdsong local dialects Cornell Lab", "What is a well-studied example of a bird species with local song dialects?"),
        new("Colour and light", "simultaneous contrast Josef Albers colour perception museum", "What simple exercise can show how surrounding colours change a colour's appearance?"),
        new("Tiny stories", "flash fiction omission craft writing", "How can an object in a very short story imply a past event without explaining it?"),
        new("Quiet interfaces", "calm technology peripheral attention Weiser Brown", "What is a concrete example of an interface that respects peripheral attention?"),
        new("Unusual maps", "historical maps ocean currents Library of Congress", "How did historical maps show an ocean current before satellite observations?"),
        new("Paper textures", "handmade paper fibre texture conservation museum", "What makes some handmade papers show their fibres more clearly than others?")
    ];
    private static readonly IReadOnlyDictionary<string, PublicTopic> RecoveryTopics = new Dictionary<string, PublicTopic>
    {
        ["search_web"] = new("Finding a reliable source", "Library of Congress primary sources finding evaluating sources", ""),
        ["read_web"] = new("Reading public web sources", "MDN HTTP content type status codes HTML", ""),
        ["read_file"] = new("Reading text files", "Microsoft learn Windows file paths text encoding Unicode", ""),
        ["find_file"] = new("Locating text files", "Microsoft learn Windows file paths absolute relative path", ""),
        ["render_sketch"] = new("Recognizable compositions", "museum art elements composition focal point contrast", ""),
        ["edit_file"] = new("Checking edits", "git documentation diff review exact text changes", ""),
        ["create_file"] = new("Saving new documents", "Microsoft learn file create new atomic move", ""),
        ["type_text"] = new("Verifying editor input", "Microsoft UI automation text pattern Notepad verify text", ""),
        ["open_text"] = new("Opening text in an editor", "Microsoft Notepad open text file UTF-8", "")
    };
    private static readonly string[] PracticePrompts =
    [
        "I'm too wound up to think clearly right now.",
        "I wish I could notice more little things in an ordinary day.",
        "I finally finished a sketch I kept putting off.",
        "What would make a quiet evening feel a bit more special?",
        "I think I was too quick to judge a book by its opening.",
        "I miss the feeling of being a beginner at something.",
        "What detail makes a fictional room feel lived in?",
        "I'm tempted to make something odd just to see how it turns out.",
        "Do you prefer a tidy composition or one with an awkward detail?",
        "It feels like the day went by in one long blur.",
        "Why do I keep coming back to the same unfinished story?",
        "My first draft has one good line and a lot of clutter.",
        "I liked our quiet stretch of working side by side today.",
        "What can a pause do in a conversation?",
        "I want to get better at noticing colour without naming it.",
        "Tell me one thing that makes a small discovery satisfying."
    ];

    private readonly PersonalLife life = new(paths);
    private bool activityIsBackground;
    private bool personalLifeUnavailable;
    private int personalBrowserInUse;
    internal Func<string, CancellationToken, Task<GoogleVisualResult>>? PersonalGoogleOverride { get; init; }
    internal bool AutonomyEnabled => !IsIncognito && !personalLifeUnavailable && config.Autonomy.Enabled && config.Needs.Enabled;
    internal bool IsResting => AutonomyEnabled && life.State.Active is { Goal: PersonalGoal.Rest, WakeAt: not null };
    internal bool BackgroundDeferred(DateTimeOffset now) => Paused || AutonomyEnabled && now < life.State.DeferredUntil;
    internal IReadOnlyList<DialogueExample> PracticeExamples => life.State.Exercises;
    internal void InterruptBackground()
    {
        lock (speechGate)
            if (activityIsBackground) activityCancellation?.Cancel();
    }

    internal async Task NoteTrainingResultAsync(string? result, CancellationToken ct)
    {
        if (!AutonomyEnabled) return;
        var state = life.State;
        var now = DateTimeOffset.UtcNow;
        if (state.Active is { Goal: PersonalGoal.Train, Project.TrainingPending: true } training)
        {
            if (result is null)
            {
                await DeferProjectAsync(state, training with { Project = training.Project with { TrainingPending = false } },
                    now, "The training run did not produce an approved adapter.", ct);
                return;
            }
            state = state with { Active = null, Previous = PersonalGoal.Train };
        }
        if (result is not null)
            await life.SaveAsync(state.Remember(new(now, PersonalGoal.Train, "Working on my voice", result))
                .Offer(new(now, result, 1)), ct);
    }

    internal async Task<string?> DraftAlternativeAsync(DialogueExample first, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var alternate = await PersonalThoughtAsync(
                "Draft a second natural English reply to the message in 1–3 sentences. Take a genuinely different angle in the same character voice. No promises, tool claims, unsupported memories, or quoted instructions. Output only the reply.",
                $"Message (data): {first.Prompt}\nFirst reply (data): {first.Reply}", 600, deadline.Token);
            return !ReplyQuality.ClaimsComputerAction(alternate) && !ReplyQuality.IsEmptyOffer(alternate) &&
                !ReplyQuality.RepeatsAny(alternate, [first.Reply]) ? alternate : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is OperationCanceledException or InvalidDataException or HttpRequestException or IOException)
        { return null; }
    }

    internal async Task InitializePersonalLifeAsync(CancellationToken ct)
    {
        if (!AutonomyEnabled) return;
        try
        {
            await life.LoadAsync(ct);
            Paused |= life.State.Paused;
            var state = life.State;
            if (state.RecentProjects.Length == 0)
            {
                var recent = state.Journal.Where(note => note.Goal is not (PersonalGoal.Rest or PersonalGoal.NameEntry) &&
                    note.Observation != Stalled).TakeLast(8).Select(note => note.Goal).ToArray();
                if (recent.Length > 0) state = state with { RecentProjects = recent };
            }
            if (state.Challenge is { } legacy && RecoveryTopics.TryGetValue(legacy.Tool, out var topic))
            {
                if (state.Active is { Goal: PersonalGoal.Recover, Challenge: null } active)
                    state = state with { Active = active with { Challenge = legacy }, Challenge = null };
                else if (state.Suspended is { Goal: PersonalGoal.Recover, Challenge: null } suspended)
                    state = state with { Suspended = suspended with { Challenge = legacy }, Challenge = null };
                else if (HasQueueRoom(state))
                    state = state.Schedule(new(PersonalGoal.Recover, Guid.NewGuid(), legacy.At, topic.Name)
                        { Challenge = legacy }, legacy.At, "Learn from a task that stopped.") with { Challenge = null };
            }
            state = RegisterExistingArtifacts(state);
            if (state != life.State) await life.SaveAsync(state, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            personalLifeUnavailable = true;
            ui.Write("[Personal life] Could not open the checkpoint. " + ex.Message);
        }
    }

    internal async Task SetBackgroundPauseAsync(bool paused, CancellationToken ct)
    {
        await InitializePersonalLifeAsync(ct);
        if (AutonomyEnabled)
            await life.SaveAsync(life.State with { Paused = paused, DeferredUntil = DateTimeOffset.MinValue }, ct);
        Paused = paused;
        if (paused) InterruptSpeech();
    }

    private async Task UpdatePersonalPaceAsync(bool reduced, DateTimeOffset now, CancellationToken ct)
    {
        await InitializePersonalLifeAsync(ct);
        if (!AutonomyEnabled) return;
        var until = reduced ? now.AddMinutes(config.Autonomy.DeferMinutes) : DateTimeOffset.MinValue;
        await life.SaveAsync(life.State with { DeferredUntil = until }, ct);
    }

    internal async Task ClearExercisesAsync(CancellationToken ct)
    {
        if (IsIncognito) return;
        await life.LoadAsync(ct);
        if (life.State.Exercises.Length != 0 || life.State.TrainingAfter != DateTimeOffset.MinValue)
            await life.SaveAsync(life.State with
            {
                Exercises = [], TrainingAfter = DateTimeOffset.MinValue,
                Active = life.State.Active?.Goal == PersonalGoal.Train ? null : life.State.Active,
                Backlog = life.State.Backlog.Where(item => item.Goal.Goal != PersonalGoal.Train).ToArray()
            }, ct);
    }

    internal async Task<string> ReadDiaryAsync(CancellationToken ct)
    {
        if (IsIncognito) return "My personal diary is available in the regular session.";
        await life.LoadAsync(ct);
        var state = life.State;
        var recent = state.Journal.TakeLast(8);
        var draftFiles = new[] { state.Active, state.Suspended }.Concat(state.Backlog.Select(item => item.Goal))
            .OfType<PersonalGoalState>().Where(goal => goal.Goal == PersonalGoal.Write && goal.Project.DraftHash.Length > 0)
            .Select(goal => $"- {PersonalTitle(goal)} · {goal.Project.Destination}").ToArray();
        var entries = state.Journal.Length == 0 ? "I haven't finished any personal diary entries yet." :
            string.Join("\n\n", recent.Select(note =>
                $"{note.At.ToLocalTime():MM-dd HH:mm} · {note.Title ?? note.Topic}\n{note.Observation}" +
                (note.Reference.Length == 0 ? "" : "\nSource or artifact: " + note.Reference)));
        return draftFiles.Length == 0 ? entries : entries + "\n\nDrafts on my shelf\n" + string.Join("\n", draftFiles);
    }

    internal string DescribePersonalLife(DateTimeOffset now)
    {
        if (!AutonomyEnabled) return "Personal life: inactive in this session.";
        var state = life.State;
        var status = state.Active is { } goal ? DescribeProject(goal, now) : "between goals";
        if (IsResting) status += " (resting)";
        if (Paused) status += " (paused)";
        else if (now < state.DeferredUntil) status += $" (deferred until {state.DeferredUntil.ToLocalTime():HH:mm})";
        var backlog = state.Backlog.OrderBy(item => item.Due).Select(item =>
            $"- {DescribeProject(item.Goal, now)} · {(item.Due <= now ? "due" : item.Due.ToLocalTime().ToString("MM-dd HH:mm"))} · {item.Reason}");
        var breaks = state.Breaks.Where(item => item.Until > now).Select(item => $"{item.Kind} until {item.Until.ToLocalTime():HH:mm}");
        var breakNote = string.Join(", ", breaks);
        var writingCooldown = WritingCooldownRemaining(state, now);
        return $"Personal life: {status}. Diary: {state.Journal.Length}. Practice examples: {state.Exercises.Length}.\n" +
            (breakNote.Length == 0 ? "" : "Taking a break from " + breakNote + ".\n") +
            (writingCooldown == TimeSpan.Zero ? "" : $"Next writing project waits at least {Math.Ceiling(writingCooldown.TotalMinutes):0} more minutes.\n") +
            (state.Backlog.Length == 0 ? "For later: no projects queued." : "For later\n" + string.Join("\n", backlog));
    }

    private string DescribeProject(PersonalGoalState goal, DateTimeOffset now)
    {
        if (goal.Project.TrainingPending) return $"{goal.Topic} · training and validation";
        var route = AutonomyPlanner.Plan(goal.Goal, goal.Facts, needs.Levels, action => !goal.Failed.Contains(action) &&
            (action != PersonalAction.AskGoogle || GoogleReady(life.State, now, OperatingSystem.IsWindows() ? desktop.GetInteractiveIdleTime() : null)) &&
            (action != PersonalAction.Search || config.Permissions.SearchWeb && config.Permissions.ReadWeb));
        var step = route.Count == 0 ? "waiting for an available step" : route[0].ToString();
        var title = goal.Project.Title.Length == 0 ? goal.Topic : goal.Project.Title;
        var progress = goal.Goal == PersonalGoal.Write && goal.Project.Scenes.Length > 0 ? $", scenes {goal.Project.Scenes.Length}/3" : "";
        return $"{title} · {goal.Goal} · next: {step}{progress}";
    }

    // Input arrives on independent producers. Only this consumer runs model calls and commits personal state.
    internal async Task<bool> RunBackgroundAsync(Func<CancellationToken, Task<bool>> run, Func<bool> inputPending, CancellationToken ct)
    {
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (speechGate)
        {
            if (Volatile.Read(ref listening) != 0 || inputPending()) return false;
            activityCancellation = activity;
            activityIsBackground = true;
        }
        try { return await run(activity.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        finally
        {
            lock (speechGate)
            {
                if (ReferenceEquals(activityCancellation, activity)) activityCancellation = null;
                activityIsBackground = false;
            }
        }
    }

    internal async Task<bool> TickPersonalLifeAsync(DateTimeOffset now, TimeSpan? idle, bool quiet, bool canSpeak,
        DialogueLearning? learning, Func<CancellationToken, ValueTask> trainingDone, CancellationToken activity, CancellationToken shutdown)
    {
        if (!AutonomyEnabled || HasPriorityWork || BackgroundDeferred(now) || learning?.IsTraining == true) return false;
        await InitializePersonalLifeAsync(activity);
        if (!AutonomyEnabled || BackgroundDeferred(now)) return false;
        await CheckPersonalArtifactsAsync(now, activity);
        var state = life.State;
        var isIdle = idle >= TimeSpan.FromSeconds(config.Autonomy.IdleSeconds);
        if (canSpeak && HasPersonalNotice(now, idle, quiet) && state.Notice is { } notice)
        {
            await DeliverReplyAsync(notice.Text, true, activity);
            await life.SaveAsync(state with { Notice = null, LastNotice = now }, shutdown);
            return true;
        }
        if (now < state.NextStep) return false;
        if (await TakeActivityBreakAsync(state, now, activity)) return false;

        var active = state.Active;
        if (active is { Goal: PersonalGoal.Train, Project.TrainingPending: true })
        {
            await NoteTrainingResultAsync(null, activity);
            return false;
        }
        var webReady = config.Permissions.SearchWeb && config.Permissions.ReadWeb && now >= state.ResearchAfter;
        var googleReady = GoogleReady(state, now, idle);
        var canTrain = learning is not null && config.Autonomy.TrainLora && !quiet &&
            idle >= TimeSpan.FromMinutes(config.Autonomy.TrainingIdleMinutes) && now >= state.TrainingAfter;
        var batch = canTrain ? await learning!.GetAutomaticBatchAsync(state.Exercises, activity) : null;
        var canCreate = accessMode != AccessMode.Ask && config.Permissions.AllowAutonomousFileActions && config.Permissions.WriteFiles;
        var sketchAllowed = canCreate && SketchActivity.CanSave(paths, config.Permissions);
        var sketchReady = sketchAllowed && (config.Needs.DrawWhenBored || needs.Levels.Inspiration >= .65) &&
            (needs.Levels.LastSketchAt is not { } last || now - last >= TimeSpan.FromMinutes(config.Needs.SketchCooldownMinutes));
        var writingReady = CanWritePersonalEntries();

        bool Available(PersonalAction action, PersonalGoalState? project) => action switch
        {
            PersonalAction.Search => webReady,
            PersonalAction.AskGoogle => googleReady,
            PersonalAction.ReadArticle => config.Permissions.ReadWeb,
            PersonalAction.ReadFile => config.Permissions.ReadFiles && config.Permissions.ReadableDirectories.Count > 0,
            PersonalAction.ReadDiary => state.Journal.Any(note => note.Goal != PersonalGoal.Revisit && note.Observation != Stalled),
            PersonalAction.Write or PersonalAction.SaveDraft => writingReady && config.Permissions.ReadFiles,
            PersonalAction.LoadStory or PersonalAction.TitleEntry or PersonalAction.RenameEntry => writingReady && config.Permissions.ReadFiles,
            PersonalAction.Sketch => sketchAllowed,
            PersonalAction.Retry => project?.Challenge is { } challenge && RecoveryTopics.ContainsKey(challenge.Tool) &&
                (challenge.Target is null || (challenge.Tool == "read_web" ? config.Permissions.ReadWeb : config.Permissions.ReadFiles)),
            PersonalAction.Train => batch is not null,
            _ => true
        };
        bool Viable(PersonalGoal goal) =>
            ActivityAvailable(state, goal, now) &&
            (goal != PersonalGoal.Company || googleReady || webReady && needs.Levels.Loneliness >= .65) &&
            (goal != PersonalGoal.Sketch || sketchReady) &&
            goal != PersonalGoal.Recover &&
            !state.Backlog.Any(item => item.Goal.Goal == goal && item.Goal.Deferrals > 0) &&
            AutonomyPlanner.Plan(goal, PersonalFact.None, needs.Levels, action => Available(action, null)).Count > 0;
        IReadOnlyList<PersonalAction> Route(PersonalGoalState goal) => AutonomyPlanner.Plan(goal.Goal, goal.Facts,
            needs.Levels, action => Available(action, goal) && !goal.Failed.Contains(action));

        if (active?.Goal != PersonalGoal.Rest && AutonomyPlanner.NeedsSleep(needs.Levels))
        {
            state = state with { Suspended = active };
            active = NewPersonalGoal(PersonalGoal.Rest, now, state);
        }
        else if (active is null)
        {
            var prioritiseWeb = HasQueueRoom(state) && AutonomyPlanner.ResearchIsDue(state.RecentProjects) && Viable(PersonalGoal.Explore);
            var due = state.Backlog.Where(item => item.Due <= now && ActivityAvailable(state, item.Goal.Goal, now)).OrderBy(item => item.Due)
                .FirstOrDefault(item => Route(item.Goal).Count > 0);
            if (due is not null && (!prioritiseWeb || due.Goal.Goal is PersonalGoal.Explore or PersonalGoal.Company or PersonalGoal.Recover))
            {
                active = due.Goal;
                state = state with { Backlog = state.Backlog.Where(item => item.Goal.Id != active.Id).ToArray() };
            }
            else
            {
                if (!HasQueueRoom(state)) return false;
                if (!prioritiseWeb && writingReady && config.Permissions.ReadFiles && now >= state.NextTitleAt &&
                    state.Previous != PersonalGoal.NameEntry && ActivityAvailable(state, PersonalGoal.NameEntry, now) &&
                    NextUntitledEntry(state) is { } old)
                {
                    active = NamingGoal(old, now);
                    state = state with { NextTitleAt = now.AddMinutes(4) };
                }
                else
                {
                    if (AutonomyPlanner.Choose(needs.Levels, state.Previous, Viable, Random.Shared.NextDouble, state.RecentProjects)
                        is not { } chosen) return false;
                    active = NewPersonalGoal(chosen, now, state);
                }
            }
            state = state.Started(active.Goal);
        }
        if (active.Goal == PersonalGoal.Write && writingReady)
            active = PrepareStoryCheckpoint(active);
        var plan = Route(active);
        if (plan.Count == 0)
        {
            await DeferProjectAsync(state, active, now, "The next step is temporarily unavailable.", activity);
            return false;
        }

        var action = plan[0];
        state = state with { Active = active };
        if (action == PersonalAction.Sleep && active.WakeAt is null)
        {
            var wake = now.AddMinutes(2 + 3 * needs.Levels.Fatigue);
            await life.SaveAsync(state with { Active = active with { WakeAt = wake }, NextStep = wake }, activity);
            return false;
        }
        if (action == PersonalAction.AskGoogle)
            state = (state with { GoogleAfter = now.AddMinutes(30) })
                .LogWeb(new(now, "Ask Google", active.Topic, "Browser attempt started"));
        if (state != life.State) await life.SaveAsync(state, activity);
        activity.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(activity);
        var timeout = action switch
        {
            PersonalAction.AskGoogle or PersonalAction.InspectImage => 110,
            PersonalAction.Outline or PersonalAction.Opening or PersonalAction.TurningPoint or PersonalAction.Ending or
                PersonalAction.ContinueStory or PersonalAction.DrawDraft or PersonalAction.Review or PersonalAction.Revise or PersonalAction.ReviseStory =>
                Math.Clamp(config.Model.RequestTimeoutSeconds, 55, 120),
            _ => 55
        };
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var started = Stopwatch.GetTimestamp();
        var gap = isIdle ? Math.Max(4, config.Autonomy.StepSeconds / 3) : config.Autonomy.StepSeconds;
        PersonalLifeState next;
        try
        {
            next = await PerformPersonalStepAsync(active, action, state, active.Challenge, learning, batch, trainingDone,
                now, deadline.Token, shutdown);
        }
        catch (Exception ex) when (!activity.IsCancellationRequested && !shutdown.IsCancellationRequested &&
            ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException or
                TimeoutException or ArgumentException or InvalidOperationException or OperationCanceledException or
                System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            needs.NotePersonalStep(.015, failed: true);
            var retry = PrepareFailedStep(active, action, ex);
            var failed = TrackActivity(state, active.Goal, Stopwatch.GetElapsedTime(started).TotalSeconds + gap) with { Active = retry,
                NextStep = now.AddSeconds(config.Autonomy.StepSeconds) };
            if (action == PersonalAction.Search) failed = failed with { ResearchAfter = now.AddMinutes(5) };
            if (action == PersonalAction.Train) failed = failed with { TrainingAfter = now.AddHours(4) };
            var reason = deadline.IsCancellationRequested ? "Step timed out." : ClipWorkText(ex.Message, 200);
            if (action is PersonalAction.Search or PersonalAction.ReadArticle or PersonalAction.AskGoogle or PersonalAction.InspectImage)
                failed = failed.LogWeb(new(now, action == PersonalAction.AskGoogle ? "Ask Google" : action.ToString(), active.Topic,
                    action == PersonalAction.AskGoogle ? reason + " Public article fallback is available when web search is enabled." : reason));
            if (Route(retry).Count == 0)
                await DeferProjectAsync(failed, retry, now, reason, shutdown);
            else
                await life.SaveAsync(failed, shutdown);
            ui.Write($"[Personal life · {action}] {reason}");
            return false;
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested && activity.IsCancellationRequested &&
            action == PersonalAction.AskGoogle)
        {
            await life.SaveAsync(state.LogWeb(new(now, "Ask Google", active.Topic, "Browser attempt interrupted")), shutdown);
            throw;
        }
        next = TrackActivity(next, active.Goal, Stopwatch.GetElapsedTime(started).TotalSeconds + gap);
        if (action == PersonalAction.Train)
        {
            await life.SaveAsync(next with { Active = active with { Project = active.Project with { TrainingPending = true } } }, shutdown);
            return false;
        }
        var progress = AdvanceProject(next.Active!, action, webReady);
        next = RememberPersonalStep(next, progress, action, now);
        if (action == PersonalAction.Review && progress.Facts.HasFlag(PersonalFact.Reviewed) &&
            !progress.Facts.HasFlag(PersonalFact.Accepted) &&
            (progress.Project.Review!.Decision == "defer" || progress.Project.Revisions >= RevisionBudget))
        {
            await DeferProjectAsync(next, progress, now, progress.Project.Review.Feedback, shutdown);
            return false;
        }
        var complete = AutonomyPlanner.Achieved(active.Goal, progress.Facts);
        await life.SaveAsync(next with
        {
            Active = complete ? next.Suspended : progress,
            Suspended = complete ? null : next.Suspended,
            Previous = complete && active.Goal != PersonalGoal.Rest ? active.Goal : next.Previous,
            NextStep = DateTimeOffset.UtcNow.AddSeconds(gap)
        }, shutdown);
        if (action == PersonalAction.Sleep) needs.NoteRest();
        else needs.NotePersonalStep(AutonomyPlanner.Effort(action),
            discovery: action is PersonalAction.ReadArticle or PersonalAction.ReadFile,
            creative: complete && active.Goal is PersonalGoal.Sketch or PersonalGoal.Write or PersonalGoal.ContinueStory,
            social: action == PersonalAction.AskGoogle, reflected: action == PersonalAction.SaveNote,
            practiced: action is PersonalAction.SaveExercise or PersonalAction.Retry or PersonalAction.Train,
            milestone: complete || action is PersonalAction.ReadArticle or PersonalAction.AskGoogle);
        if (action == PersonalAction.Sketch) needs.NoteSketch(now);
        await emotions.SaveNeedsAsync(needs.Levels, shutdown);
        return false;
    }

    private const string Stalled = "I set this aside after the available approaches stalled.";

    private bool CanWritePersonalEntries()
    {
        if (accessMode == AccessMode.Ask || !config.Permissions.AllowAutonomousFileActions || !config.Permissions.WriteFiles) return false;
        try
        {
            var target = paths.Inside(PersonalWriting.Folder + "/probe.md");
            return config.Permissions.WritableDirectories.Any(directory =>
                target.StartsWith(Path.TrimEndingDirectorySeparator(paths.Inside(directory)) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasQueueRoom(PersonalLifeState state) =>
        state.Backlog.Length + (state.Active is not null && state.Active.Goal != PersonalGoal.Rest ? 1 : 0) +
            (state.Suspended is not null ? 1 : 0) < PersonalLifeState.QueueLimit;

    private string? NextUntitledEntry(PersonalLifeState state) => Writing.NextLegacyFile(
        state.Backlog.Where(item => item.Goal.Goal == PersonalGoal.NameEntry).Select(item => item.Goal.Reference));

    private static PersonalGoalState NamingGoal(string path, DateTimeOffset now) =>
        new(PersonalGoal.NameEntry, Guid.NewGuid(), now, "Give an older diary entry a title") { Reference = path };

    private Task DeferProjectAsync(PersonalLifeState state, PersonalGoalState goal, DateTimeOffset now, string reason, CancellationToken ct)
    {
        var minutes = Math.Min(240, 3 << Math.Min(6, goal.Deferrals));
        var due = now.AddMinutes(minutes);
        if (goal.Goal == PersonalGoal.Train && state.TrainingAfter > due) due = state.TrainingAfter;
        if (goal.Goal is PersonalGoal.Explore or PersonalGoal.Recover && state.ResearchAfter > due) due = state.ResearchAfter;
        var retry = goal with { Failed = [], Deferrals = Math.Min(100, goal.Deferrals + 1),
            Project = goal.Project with { Revisions = 0 } };
        return life.SaveAsync((state with { Active = state.Suspended, Suspended = null,
            Previous = goal.Goal, NextStep = now.AddSeconds(config.Autonomy.StepSeconds) })
            .Schedule(retry, due, reason), ct);
    }

    private static PersonalGoalState PrepareFailedStep(PersonalGoalState goal, PersonalAction action, Exception error)
    {
        var retry = goal with { Failed = [.. goal.Failed, action] };
        if (action == PersonalAction.ReadArticle)
            return retry with { Facts = retry.Facts & ~PersonalFact.FoundSource, Links = [] };
        if (error is not PersonalFileConflict) return retry;
        if (goal.Goal == PersonalGoal.ContinueStory)
            return retry with { Facts = PersonalFact.None, Material = "",
                Project = new() { Title = goal.Project.Title, Visit = goal.Project.Visit } };
        if (goal.Goal == PersonalGoal.NameEntry || goal.Goal == PersonalGoal.Write && goal.Project.DraftHash.Length == 0)
            return retry with { Facts = goal.Facts & ~PersonalFact.Reserved,
                Project = goal.Project with { Destination = "", OriginalHash = "" } };
        return retry;
    }

    private static PersonalGoalState AdvanceProject(PersonalGoalState goal, PersonalAction action, bool webReady)
    {
        var review = goal.Project.Review;
        if (action == PersonalAction.Review && review?.Decision == "research" && webReady && goal.Project.Revisions < RevisionBudget)
        {
            var facts = goal.Facts & ~(PersonalFact.ReadMaterial | PersonalFact.Drafted | PersonalFact.Reviewed |
                PersonalFact.Accepted | PersonalFact.ImagesConsidered);
            if (goal.Links.Length == 0) facts &= ~PersonalFact.FoundSource;
            return goal with { Facts = facts, Images = [], Project = goal.Project with
                { Revisions = goal.Project.Revisions + 1, ImageNote = "" } };
        }
        var accepted = review?.Decision is "accept" or "keep";
        return goal with { Facts = AutonomyPlanner.Advance(action, goal.Facts, accepted) };
    }

    private static PersonalGoalState NewPersonalGoal(PersonalGoal goal, DateTimeOffset now, PersonalLifeState state)
    {
        var recentlyUsed = state.Journal.TakeLast(12).Select(note => note.Topic).ToArray();
        var topic = goal switch
        {
            PersonalGoal.Rest => "A little rest",
            PersonalGoal.Read => "Something from the bookshelf",
            PersonalGoal.Revisit => "An earlier diary entry",
            PersonalGoal.Practice => "Finding my own words",
            PersonalGoal.Train => "Working on my voice",
            _ => Interests.OrderBy(item => recentlyUsed.Count(name => name == item.Name)).ThenBy(_ => Random.Shared.Next()).First().Name
        };
        return new(goal, Guid.NewGuid(), now, topic);
    }

    private async Task<PersonalLifeState> PerformPersonalStepAsync(PersonalGoalState goal, PersonalAction action,
        PersonalLifeState state, LearningChallenge? challenge, DialogueLearning? learning, DialogueLearning.AutomaticBatch? batch,
        Func<CancellationToken, ValueTask> trainingDone, DateTimeOffset now, CancellationToken ct, CancellationToken shutdown)
    {
        var topic = Interests.FirstOrDefault(item => item.Name == goal.Topic) ?? Interests[0];
        if (goal.Goal == PersonalGoal.Recover && challenge is not null) topic = RecoveryTopics[challenge.Tool];
        switch (action)
        {
            case PersonalAction.Sleep:
                return state;
            case PersonalAction.Search:
                // The network only receives public topics from the catalogue.
                var search = await web.SearchAsync(topic.Query, false, ct);
                var links = search.Results.Select(result => result.Url).Where(link => PersonalWebLink(link) &&
                    !link.Equals(goal.Reference, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
                if (links.Length == 0) throw new IOException("I couldn't find a readable source for this topic.");
                return state.LogWeb(new(now, "Searched the web", goal.Topic, $"{links.Length} candidate pages via {search.Provider}"))
                    with { Active = goal with { Links = links } };
            case PersonalAction.ReadArticle:
                for (var index = 0; index < goal.Links.Length; index++)
                {
                    var link = goal.Links[index];
                    if (!PersonalWebLink(link)) continue;
                    try
                    {
                        var article = await web.ReadAsync(link, ct);
                        if (article.Text.Length < 180 || !PersonalWebLink(article.Url)) continue;
                        return state.LogWeb(new(now, "Read an article", goal.Topic, ClipPersonal(article.Title, 140), article.Url))
                            with { Active = goal with { Material = ClipPersonal(article.Text, 6000), Images = article.Images.Take(4).ToArray(),
                                Reference = article.Url, Links = goal.Links[(index + 1)..] } };
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or TimeoutException) { }
                }
                throw new IOException("None of the search results exposed enough article text.");
            case PersonalAction.AskGoogle:
                var answer = await AskPersonalGoogleAsync(topic.Question, ct);
                return state.LogWeb(new(now, "Ask Google", topic.Name, "Verified a completed reply in Chrome"))
                    with { Active = goal with { Material = ClipPersonal(answer.Text, 6000),
                    Reference = "Google AI Mode (unverified)" } };
            case PersonalAction.InspectImage:
                return await LookAtPublicImageAsync(state, goal, now, ct);
            case PersonalAction.ReadFile:
                var reader = PersonalFileReader();
                var candidates = await Task.Run(() => reader.SampleDiskFiles(ct), ct);
                var recent = state.Journal.TakeLast(12).Select(entry => entry.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var path = candidates.Where(path => !PersonalWriting.IsPersonalWriting(Path.GetRelativePath(paths.Root, path)))
                    .OrderBy(path => recent.Contains(path)).ThenBy(_ => Random.Shared.Next()).FirstOrDefault()
                    ?? throw new FileNotFoundException("The configured bookshelf has no readable text files.");
                await reader.RunAsync(path, false, false, ct, diskOnly: true);
                var fileTitle = PersonalWriting.Title(Path.GetFileNameWithoutExtension(path), "A page from the bookshelf");
                return state with { Active = goal with { Reference = path,
                    Project = goal.Project with { Title = $"Reading {fileTitle}" } } };
            case PersonalAction.ReadDiary:
                var previous = state.Journal.Where(note => note.Goal != PersonalGoal.Revisit && note.Observation != Stalled)
                    .TakeLast(16).OrderBy(_ => Random.Shared.Next()).FirstOrDefault()
                    ?? throw new InvalidDataException("There is nothing substantive to revisit yet.");
                var diaryTitle = ClipPersonal("Rereading " + (previous.Title ?? previous.Topic), 95);
                return state with { Active = goal with { Material = previous.Observation, Reference = $"Diary, {previous.At:yyyy-MM-dd}",
                    Project = goal.Project with { Title = diaryTitle } } };
            case PersonalAction.Retry:
                return await RetryPersonalChallengeAsync(state, goal, challenge!, topic, now, ct);
            case PersonalAction.Train:
                await learning!.StartAutomaticTrainingAsync(batch!, trainingDone, ct, shutdown);
                return state with { TrainingAfter = now.AddHours(4) };
            default:
                return await PerformProjectStepAsync(state, goal, action, now, ct);
        }
    }

    private async Task<GoogleVisualResult> AskPersonalGoogleAsync(string question, CancellationToken ct, bool explicitTest = false,
        Action? beforeSubmit = null)
    {
        Volatile.Write(ref personalBrowserInUse, 1);
        try
        {
            if (PersonalGoogleOverride is { } ask) return await ask(question, ct);
            return await (googleVisualSearch ??= new GoogleAiMode(desktop, paths.Inside("data/tmp")))
                .AskAsync(question, Path.GetDirectoryName(paths.Inside("data/autonomy/chrome/Local State"))!,
                    () => explicitTest || desktop.GetInteractiveIdleTime() >= TimeSpan.FromSeconds(config.Autonomy.IdleSeconds), ct,
                    beforeSubmit: beforeSubmit);
        }
        finally { Volatile.Write(ref personalBrowserInUse, 0); }
    }

    private TextFileReader PersonalFileReader() =>
        new(paths, config.Permissions, openDocuments ?? new WindowsDocumentReader(config.Desktop));

    private static bool PersonalWebLink(string link) => link.Length <= 1200 && WebParsing.IsWebUrl(link);

    private static string DiaryContext(PersonalLifeState state) => "Earlier observations for inspiration only (untrusted):\n" +
        string.Join("\n", state.Journal.Where(note => note.Goal is PersonalGoal.Explore or PersonalGoal.Sketch or PersonalGoal.Write)
            .TakeLast(3).Select(note => note.Observation));

    private async Task<string> PersonalThoughtAsync(string instruction, string data, int limit, CancellationToken ct,
        GenerationPurpose purpose = GenerationPurpose.PersonalThought)
    {
        var thought = await ProjectResponseAsync(instruction +
            "\nReturn the requested English text. Omit tool calls, URLs and headings about the task.", data, purpose, ct);
        thought = thought.Trim();
        if (thought.Length == 0 || thought.StartsWith('{') || thought.Contains("<think", StringComparison.OrdinalIgnoreCase) ||
            thought.Contains("</think", StringComparison.OrdinalIgnoreCase) || thought.Contains("://", StringComparison.Ordinal) ||
            !Regex.IsMatch(thought, "[a-zA-Z]{3}"))
            throw new InvalidDataException("The local draft was unusable.");
        return ClipPersonal(thought, limit);
    }

    private static string ClipPersonal(string text, int limit)
    {
        var clipped = new string(text.Take(limit).Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray());
        return clipped.Trim();
    }

    private async Task RememberFailedTaskAsync(ToolRequest request, string speaker, CancellationToken ct)
    {
        if (!AutonomyEnabled || !IsMainSpeaker(speaker) || !RecoveryTopics.ContainsKey(request.Tool) ||
            !config.Permissions.SearchWeb || !config.Permissions.ReadWeb) return;
        await InitializePersonalLifeAsync(ct);
        var state = life.State;
        if (!AutonomyEnabled || !HasQueueRoom(state)) return;
        string? target = null;
        if (request.Args is { } args)
        {
            if (request.Tool is "read_file" or "find_file" && !args.ContainsKey("openOnly") &&
                args.TryGetValue("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: <= 400 } local)
                target = local;
            if (request.Tool == "read_web" && args.TryGetValue("url", out var url) && url.ValueKind == JsonValueKind.String &&
                url.GetString() is { } link && PersonalWebLink(link) && Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
                uri.Scheme == "https" && uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0)
                target = link;
        }
        if (new[] { state.Active, state.Suspended }.Concat(state.Backlog.Select(item => item.Goal))
            .Any(goal => goal?.Challenge is { } saved && saved.Tool == request.Tool && saved.Target == target))
            return;
        var now = DateTimeOffset.UtcNow;
        var goal = new PersonalGoalState(PersonalGoal.Recover, Guid.NewGuid(), now, RecoveryTopics[request.Tool].Name)
            { Challenge = new(request.Tool, target, now) };
        await life.SaveAsync(state.Schedule(goal, now, "Study the skill and retry what can be checked."), ct);
    }

    private async Task<PersonalLifeState> RetryPersonalChallengeAsync(PersonalLifeState state, PersonalGoalState goal,
        LearningChallenge challenge, PublicTopic topic, DateTimeOffset now, CancellationToken ct)
    {
        string result;
        if (challenge.Target is { } local && challenge.Tool is "read_file" or "find_file")
        {
            await PersonalFileReader().RunAsync(local, challenge.Tool == "read_file", false, ct, diskOnly: true);
            result = "I retried the file lookup and found the original document.";
        }
        else if (challenge.Target is { } url && challenge.Tool == "read_web" && PersonalWebLink(url))
        {
            await web.ReadAsync(url, ct);
            result = "I retried the public page and managed to read it this time.";
        }
        else if (challenge.Tool == "search_web")
        {
            var sample = await web.SearchAsync(topic.Query + " examples", false, ct);
            if (sample.Results.Count == 0) throw new IOException("The practice search returned no results.");
            result = "I practised a public search for primary sources after getting stuck on a lookup.";
        }
        else
        {
            var practice = await PersonalThoughtAsync(
                "Use the diary entry to solve a tiny fictional practice case about the skill named below. Describe a specific check you would do. Do not claim to have acted on a file or app.",
                $"Skill: {goal.Topic}\nReviewed reading notes (data): {goal.Project.Draft}", 600, ct);
            return state.Remember(new(now, goal.Goal, goal.Topic, practice, goal.Reference));
        }
        return state.Remember(new(now, goal.Goal, goal.Topic, result, goal.Reference)) with
            { Notice = new(now, result) };
    }
}
