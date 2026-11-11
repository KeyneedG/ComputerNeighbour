using ComputerNeighbour.Memory;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private MemoryEntry? LatestUserTurn() => memory.Recent.LastOrDefault(entry => entry.Role == "user");

    private MemoryEntry? LastActionRequest(bool beforeLatest = false)
    {
        var turns = memory.Recent.Where(entry => entry.Role == "user");
        if (beforeLatest) turns = turns.SkipLast(1);
        var latest = turns.LastOrDefault(entry => !IsMainSpeaker(entry.SpeakerId) ||
            !entry.Text.TrimStart().StartsWith('/') && !IsWorkStatus(entry.Text));
        if (latest is null || !IsMainSpeaker(latest.SpeakerId)) return null;
        if (memory.Tasks.LastRecovery is not { } recovery || latest.At != recovery.FollowupAt) return latest;
        return turns.LastOrDefault(entry => entry.At == recovery.RequestAt && IsMainSpeaker(entry.SpeakerId));
    }

    private bool MatchesActionRequest(CurrentWorkflow task, MemoryEntry source) =>
        IsMainSpeaker(task.Speaker) && (task.PublicationAnchor == source.At || task.Google?.OwnerTurn == source.At ||
            task.Request.Trim().Equals(ClipWorkText(source.Text.Trim(), 7900), StringComparison.OrdinalIgnoreCase));

    private bool CanRetryCurrentFromChat(CurrentWorkflow task) =>
        model is not Inference.IReplyTaskModel ||
        (LastActionRequest() is { } source ? MatchesActionRequest(task, source) : LatestUserTurn() is null);

    private async Task RecoverOwnerActionAsync(MemoryEntry? source, string confirmation, CancellationToken ct,
        MemoryEntry? followup = null)
    {
        if (source is null || !IsMainSpeaker(source.SpeakerId) || source.Text.TrimStart().StartsWith('/') ||
            string.IsNullOrWhiteSpace(OwnerActionText(source.Text)))
        {
            await DeliverReplyAsync("Give me the task you want me to carry out, including what to act on.", false, ct);
            return;
        }
        if (currentThought is { } thought)
        {
            thought.Route = "action_recovery";
            thought.Check("Rechecked the main user's original request for an action.");
        }

        var state = memory.Tasks;
        if (followup is not null && followup.At != source.At &&
            !await memory.SaveTasksAsync(state, state with { LastRecovery = new(followup.At, source.At) }, ct))
        {
            await DeliverReplyAsync(TaskChanged, false, ct);
            return;
        }
        if (state.Current is { } current && MatchesActionRequest(current, source))
        {
            if (current.Pending) await TryCurrentWorkAsync("/task retry", ct, null);
            else await DeliverReplyAsync(current.Detail, false, ct);
            return;
        }
        if (state.BrowserPublication is { Pending: false } published && MatchesActionRequest(published, source))
        {
            await DeliverReplyAsync(published.Detail, false, ct);
            return;
        }
        if (memory.LastArtifact is { } artifact && IsMainSpeaker(artifact.SpeakerId) &&
            artifact.Goal.Equals(ClipWorkText(source.Text, 1500), StringComparison.Ordinal))
        {
            await DeliverReplyAsync("I already recorded a result for that request at " + artifact.Path + ".", false, ct);
            return;
        }
        if (state.Items.FirstOrDefault(item => item.Workflow is { } workflow && MatchesActionRequest(workflow, source)) is { } saved)
        {
            var reply = await ResumeSavedWorkflowAsync(saved, saved.Workflow!, ct);
            if (reply is not null) await DeliverReplyAsync(reply, false, ct);
            return;
        }
        PrunePendingActions();
        if (pending.Values.Any(item => item.Work is not null || item.Change is not null))
        {
            await DeliverReplyAsync("An action is waiting for approval. Resolve it before I start a new action.", false, ct);
            return;
        }
        if (await ReviewWrittenReplyAsync(source.Text, "", ct, original: source, confirmation: confirmation) == ReplyAudit.Handled)
        {
            if (memory.Tasks.Current is { Execution: not null } started && MatchesActionRequest(started, source) &&
                state.Items.FirstOrDefault(item => item.Workflow is null && item.Goal == ClipWorkText(source.Text, 500)) is { } note)
                await RemoveSavedWorkflowAsync(note, ct);
            return;
        }
        await DeliverReplyAsync("I couldn't identify an executable action in that request. Tell me what to do and what to act on.", false, ct);
    }
}
