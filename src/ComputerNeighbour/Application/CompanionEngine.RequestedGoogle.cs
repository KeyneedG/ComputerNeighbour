using ComputerNeighbour.Memory;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private CurrentWorkflow? RecordedGoogleRequest(MemoryEntry owner) =>
        new[] { memory.Tasks.Current, memory.Tasks.QueuedGoogle }.FirstOrDefault(task => task?.Google?.OwnerTurn == owner.At);

    private async Task<bool> TryIntakeGoogleRequestAsync(string input, CancellationToken ct)
    {
        if (!GoogleService.IsMatch(input) || memory.Recent.LastOrDefault(entry => entry.Role == "user") is not { } owner ||
            !IsGroundedGoogleEvidence(input, await ReadReplyTaskAsync(input, null, [], ct))) return false;
        await DeliverReplyAsync(await QueueRequestedGoogleAsync(owner, ct), false, ct);
        return true;
    }

    private async Task<string> QueueRequestedGoogleAsync(MemoryEntry owner, CancellationToken ct)
    {
        var state = memory.Tasks;
        var existing = RecordedGoogleRequest(owner) ?? (state.Current is { Pending: true, Google: not null } active
            ? active : state.QueuedGoogle);
        if (existing is not null) return existing.Detail;

        var detail = state.Current?.Pending == true ? "I saved the Google AI conversation behind the current task." :
            GoogleConfigured ? $"I saved the Google AI conversation as my next task. I'll start when Chrome is available and the desktop has been idle for {config.Autonomy.IdleSeconds} seconds." :
            "I saved the Google AI conversation as my next task. It needs Chrome and desktop access.";
        var task = new CurrentWorkflow(ClipWorkText(owner.Text, 7900), owner.SpeakerId, WorkflowStatus.Blocked, detail,
            Google: new(owner.At, DateTimeOffset.UtcNow));
        var next = state.Current?.Pending == true ? state with { QueuedGoogle = task } : state with { Current = task };
        if (!await memory.SaveTasksAsync(state, next, ct)) return TaskChanged;
        if (currentThought is { } thought)
        {
            thought.Route = "google_ai_task";
            thought.Check("I saved the owner's request for a Google AI conversation.");
        }
        return detail;
    }

    internal async Task<bool> TickRequestedGoogleAsync(DateTimeOffset now, TimeSpan? idle, CancellationToken ct)
    {
        if (IsIncognito) return false;
        var state = memory.Tasks;
        if (state.Current?.Pending != true && state.QueuedGoogle is { } queued)
        {
            var detail = GoogleConfigured ? "Waiting for Chrome and desktop idle." : "Waiting for Chrome and desktop access.";
            if (!await memory.SaveTasksAsync(state, state with { Current = queued with { Detail = detail }, QueuedGoogle = null }, ct))
                return false;
        }
        if (memory.Tasks.Current is not { Status: WorkflowStatus.Blocked, Google.RetryAt: { } due } task ||
            now < due || !GoogleConfigured || BackgroundDeferred(now) ||
            idle is null || idle < TimeSpan.FromSeconds(config.Autonomy.IdleSeconds)) return false;
        await RunRequestedGoogleAsync(task, now, ct, manual: false);
        return true;
    }

    private async Task RunRequestedGoogleAsync(CurrentWorkflow task, DateTimeOffset now, CancellationToken ct, bool manual)
    {
        if (!GoogleConfigured)
        {
            await ReplyToTaskAsync("The Google AI task is saved. It needs Chrome and desktop access.", ct);
            return;
        }
        await InitializePersonalLifeAsync(ct);
        var lastTopic = life.State.WebHistory.LastOrDefault(item => item.Step == "Ask Google")?.Topic;
        var topic = Interests.FirstOrDefault(item => item.Question == task.Google!.Question) ??
            Interests.First(item => item.Name != lastTopic);
        var google = task.Google! with { Question = topic.Question, RetryAt = null };
        task = task with { Status = WorkflowStatus.Running, Google = google,
            MayHaveChangedComputer = true, Detail = "Opening Google AI Mode in Chrome." };
        await SaveCurrentWorkAsync(task, ct);
        ui.Write("[Work] " + task.Detail);
        var sending = 0;
        string answer;
        try
        {
            var result = await AskPersonalGoogleAsync(topic.Question, ct, explicitTest: manual,
                beforeSubmit: () => Volatile.Write(ref sending, 1));
            answer = ClipPersonal(result.Text, 6000);
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Chrome did not expose an answer.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            var uncertain = Volatile.Read(ref sending) != 0 || ex is OperationCanceledException;
            var detail = uncertain ? "I couldn't verify a Google AI reply. The question may already have been sent." :
                "The Google AI task is saved for retry. " + ClipWorkText(ex.Message, 360);
            await SaveCurrentWorkAsync(task with { Status = WorkflowStatus.Blocked, Detail = detail, MayHaveChangedComputer = uncertain,
                Google = google with { RetryAt = uncertain ? null : now.AddMinutes(3) } }, CancellationToken.None);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            await ReplyToTaskAsync(detail, ct);
            return;
        }

        var finished = "Google AI replied in Chrome. " + ClipWorkText(answer, 650);
        await SaveCurrentWorkAsync(task with { Status = WorkflowStatus.Finished, Detail = finished }, CancellationToken.None);
        if (!personalLifeUnavailable)
        {
            var state = life.State.LogWeb(new(now, "Ask Google", topic.Name, "Verified a completed reply in Chrome"))
                .Think(new(now, topic.Name, "Google AI reply", ClipPersonal(answer, 1200)));
            await life.SaveAsync(state, ct);
        }
        await ReplyToTaskAsync(finished, ct);
    }
}
