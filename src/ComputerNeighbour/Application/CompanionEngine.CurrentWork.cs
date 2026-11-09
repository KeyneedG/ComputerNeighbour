using System.Text.RegularExpressions;
using ComputerNeighbour.Internet;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private const RegexOptions WorkWords = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private WorkRun? resumableWork;
    internal bool HasPriorityWork => !IsIncognito && memory.Tasks.Current is { Pending: true } current &&
        (current.Google is null || current.Status == WorkflowStatus.Running ||
            current.Google.RetryAt is { } due && GoogleConfigured && due <= DateTimeOffset.UtcNow);

    internal async Task RestoreCurrentWorkAsync(CancellationToken ct)
    {
        if (IsIncognito) return;
        if (memory.Tasks.Current is { Status: WorkflowStatus.Running or WorkflowStatus.Approval } current)
        {
            await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.Blocked,
                Detail = current.Google is not null ? "The browser session ended before I verified a Google AI reply. The question may already have been sent."
                    : BrowserPublicationTarget(current.Request) is not null
                    ? current.MayHaveChangedComputer ? PublicationUnconfirmed : "The browser task was interrupted. Say ‘try again’ to resume it."
                    : "The session ended before I could finish. Any desktop change needs checking before another attempt." }, ct);
            return;
        }
        if (PreviouslyApprovedPublication() is { } missed && memory.Tasks.Current?.Request != missed.Request &&
            memory.Tasks.BrowserPublication?.Request != missed.Request)
        {
            await SaveCurrentWorkAsync(await PrepareWritingExcerptAsync(missed.Request, "user", missed, ct), ct);
            return;
        }
        if (await RecoverMissedPublicationAsync(ct) is { } recovered)
        {
            await SaveCurrentWorkAsync(recovered, ct, consumeOffer: true);
            return;
        }
        if (memory.Tasks.Current is null)
        {
            // Recover the earlier explicit X note request when this version first sees it.
            var recent = memory.Recent.Where(entry => entry.Role == "user" && IsMainSpeaker(entry.SpeakerId)).TakeLast(12).ToArray();
            var earlier = recent.LastOrDefault(entry => BrowserPublicationTarget(entry.Text) is not null);
            if (earlier is not null && recent.SkipWhile(entry => entry != earlier).Skip(1).All(entry => IsWorkRetry(entry.Text) || IsWorkStatus(entry.Text)))
                await SaveCurrentWorkAsync(await PreparePublicationNoteAsync(earlier.Text, earlier.SpeakerId, ct), ct);
        }
        else if (memory.Tasks.Current is { Status: WorkflowStatus.Draft or WorkflowStatus.ComposerOpened } draft &&
            BrowserPublicationTarget(draft.Request) is not null)
        {
            var detail = draft.Status == WorkflowStatus.ComposerOpened
                ? "The previous version opened the composer. Say ‘try again’ to run the saved task, or report its URL if you already posted it."
                : "The note is ready. Say ‘try again’ to continue in your signed-in browser.";
            if (draft.Detail != detail) await SaveCurrentWorkAsync(draft with { Detail = detail }, ct);
        }
    }

    private async Task SaveCurrentWorkAsync(CurrentWorkflow current, CancellationToken ct, bool consumeOffer = false)
    {
        var state = memory.Tasks;
        var publication = current.Google is null && BrowserPublicationTarget(current.Request) is not null ? current : state.BrowserPublication;
        var queuedGoogle = state.Current is { Pending: true, Google: { } google } active && current.Google?.OwnerTurn != google.OwnerTurn
            ? active : state.QueuedGoogle;
        var next = state with { Current = current, BrowserPublication = publication,
            PublicationOffer = consumeOffer ? null : state.PublicationOffer, QueuedGoogle = queuedGoogle };
        if (state.Current is { Pending: true, Google: null } displaced && displaced.Request != current.Request)
        {
            var archived = state.Items.FirstOrDefault(task => task.Workflow?.Request == displaced.Request) ??
                new WorkTask(UniqueWorkTaskTitle(state, ClipWorkText(Regex.Replace(displaced.Request, @"\s+", " "), 85)),
                    ClipWorkText(displaced.Request, 500), "", "", []);
            next = next.WithTask(archived with
            {
                Workflow = displaced,
                Progress = displaced.Detail,
                NextStep = displaced.MayHaveChangedComputer ? "Check the effect of the last attempt before resuming." : "Continue the saved request."
            }, activate: false);
        }
        if (!await memory.SaveTasksAsync(state, next, ct))
            throw new InvalidOperationException("The current task changed. Check /task current.");
    }

    private async Task UpdateCurrentWorkAsync(WorkRun run, WorkflowStatus status, string detail, CancellationToken ct)
    {
        if (!ReferenceEquals(run, resumableWork) || memory.Tasks.Current is not { } current) return;
        await SaveCurrentWorkAsync(current with { Status = status, Detail = ClipWorkText(detail, 950) }, ct);
    }

    internal string DescribeCurrentWork() => memory.Tasks.Current is { } current
        ? $"Current task · {current.Request}\n{current.Status} · {current.Detail}" +
            (current.Draft is { } draft ? "\nDraft · " + draft : "") +
            (memory.Tasks.QueuedGoogle is not null ? "\nNext task · Google AI conversation" : "") : "There is no current workflow.";

    private static bool IsWorkRetry(string input) => Regex.IsMatch(input.Trim(),
        @"^(?:(?:ok(?:ay)?|so|please|yes|да|хорошо|тогда|пожалуйста)[,\s]+)*(?:(?:go on and )?try again|retry(?: the task)?|continue|resume(?: the task)?|go (?:on|ahead)|(?:can|could|would) (?:you )?(?:do it|try(?: it)? again)(?: now)?|продолжи|попробуй (?:снова|ещ[её] раз)|(?:можешь )?сделай это сейчас)[.?!\s]*$", WorkWords);

    private static bool IsWorkStatus(string input) => IsPublicationStatusCorrection(input) || Regex.IsMatch(input.Trim(),
        @"^(?:why (?:did )?you stop(?:ped)?|why did it stop|what happened(?: to (?:it|the task))?|are you (?:doing it|working on it)|but you're not doing anything|почему (?:ты )?остановил\p{L}*|что с задачей)[.?!\s]*$", WorkWords);

    private bool IsCurrentWorkTurn(string input) => !IsIncognito &&
        (input.Trim().Equals("/task current", StringComparison.OrdinalIgnoreCase) ||
         model is not Inference.IPublicationIntentModel && BrowserPublicationTarget(input) is not null ||
         memory.Tasks.Current is { } current && IsMainSpeaker(current.Speaker) &&
         (IsWorkStatus(input) || (model is not Inference.IPublicationIntentModel || BrowserPublicationTarget(current.Request) is null) &&
          (current.Pending || BrowserPublicationTarget(current.Request) is not null) && IsWorkRetry(input) && CanRetryCurrentFromChat(current) ||
          input.Trim().Equals("/task retry", StringComparison.OrdinalIgnoreCase) ||
          current.Pending && BrowserPublicationTarget(current.Request) is not null && (input.Trim().Equals("/task compose", StringComparison.OrdinalIgnoreCase) ||
              input.Trim().StartsWith("/task posted ", StringComparison.OrdinalIgnoreCase))));

    internal async Task RejectFromConsoleAsync(int id, CancellationToken ct)
    {
        if (pending.TryGetValue(id, out var item) && item.Work is { } run)
            await UpdateCurrentWorkAsync(run, WorkflowStatus.Blocked, "The proposed action was rejected. Use /task retry to review the next step.", ct);
        Reject(id);
    }

    private async Task<bool> TryCurrentWorkAsync(string input, CancellationToken ct, PublicationContext? publication)
    {
        if (IsIncognito) return false;
        var request = input.Trim();
        if (request.Equals("/task current", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return true;
        }
        if (publication?.Issue is { } issue)
        {
            await ReplyToTaskAsync(issue, ct);
            return true;
        }
        if (publication?.Retry == true)
        {
            if (memory.Tasks.Current is not { } linked || BrowserPublicationTarget(linked.Request) is not { } target) return false;
            linked = AnchorPublication(linked);
            await SaveCurrentWorkAsync(linked, ct);
            if (publication.StatusOnly || !linked.Pending) await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            else await RunBrowserPublicationAsync(linked, target, ct);
            return true;
        }
        var publicationRequest = publication?.Request ?? request;
        if ((publication is not null || model is not Inference.IPublicationIntentModel) &&
            BrowserPublicationTarget(publicationRequest) is { } destination)
        {
            var writing = RequestsWritingPublication(publicationRequest);
            var previous = memory.Tasks.BrowserPublication ?? memory.Tasks.Current;
            var sameSite = previous is not null && BrowserPublicationTarget(previous.Request) is { } previousSite && SamePublicationSite(destination, previousSite);
            var sameKind = sameSite && writing == RequestsWritingPublication(previous!.Request);
            var another = publication?.Another == true || WantsAnotherPublication(publicationRequest);
            var excluded = another && sameKind && previous is { Status: WorkflowStatus.Finished } ? previous.Draft : null;
            var sign = publication?.Sign == true;
            var prepared = publication?.Prepared ?? (writing || sign || publication?.Preview == true
                ? await PreparePublicationDraftAsync(publicationRequest, currentSpeaker, publication, ct, sign, excluded) : null);
            if (publication?.Preview == true)
            {
                prepared ??= await PreparePublicationDraftAsync(publicationRequest, currentSpeaker, publication, ct, sign, excluded);
                if (model is Inference.IPublicationIntentModel)
                {
                    var state = memory.Tasks;
                    var offer = prepared.Draft is null ? null : AnchorPublication(prepared);
                    if (!await memory.SaveTasksAsync(state, state with { PublicationOffer = offer }, ct))
                        throw new InvalidOperationException("The publication offer changed.");
                }
                await DeliverReplyAsync(prepared.Draft is null ? prepared.Detail : prepared.Detail + " Would you like me to post it on " + destination.Host + "?", false, ct);
                return true;
            }
            PrunePendingActions();
            if (pending.Values.Any(item => item.Work is not null))
                await ReplyToTaskAsync("A workflow is waiting for approval. Resolve it or use /cancel before starting a new one.", ct);
            else
            {
                var sameMaterial = sameKind && (prepared is null || prepared.Draft is not null && previous!.Draft == prepared.Draft);
                if (sameMaterial && previous is { Status: WorkflowStatus.Finished } && !another)
                {
                    await SaveCurrentWorkAsync(AnchorPublication(previous), ct, consumeOffer: true);
                    await ReplyToTaskAsync(previous.Detail, ct);
                }
                else
                {
                    var selected = sameMaterial && previous is { Pending: true } && (previous.Draft is not null || previous.MayHaveChangedComputer) ? previous :
                        sameMaterial && previous is { Status: WorkflowStatus.Cancelled, MayHaveChangedComputer: true }
                            ? previous with { Status = WorkflowStatus.Blocked, Detail = PublicationUnconfirmed }
                            : prepared ?? await PreparePublicationDraftAsync(publicationRequest, currentSpeaker, publication, ct, sign, excluded);
                    resumableWork = null;
                    await SaveCurrentWorkAsync(AnchorPublication(selected), ct, consumeOffer: true);
                    if (publication?.StatusOnly == true) await ReplyToTaskAsync(DescribeCurrentWork(), ct);
                    else if (selected.Draft is null && !selected.MayHaveChangedComputer) await ReplyToTaskAsync(selected.Detail, ct);
                    else await RunBrowserPublicationAsync(memory.Tasks.Current!, destination, ct);
                }
            }
            return true;
        }
        if (memory.Tasks.Current is not { } current || !IsMainSpeaker(current.Speaker)) return false;
        if (IsWorkStatus(request))
        {
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return true;
        }
        if (current.Pending && BrowserPublicationTarget(current.Request) is { } site)
        {
            if (request.Equals("/task compose", StringComparison.OrdinalIgnoreCase))
            {
                await RunBrowserPublicationAsync(current, site, ct);
                return true;
            }
            if (request.StartsWith("/task posted ", StringComparison.OrdinalIgnoreCase))
            {
                var link = request[13..].Trim();
                if (!(current.Status == WorkflowStatus.ComposerOpened || current.MayHaveChangedComputer) || !ValidPublicationLink(link, site))
                    await ReplyToTaskAsync("After verifying the post, use /task posted followed by its exact URL.", ct);
                else
                {
                    await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.Finished, Detail = "You confirmed the publication at " + link }, ct);
                    await ReplyToTaskAsync("I recorded the post URL and closed the task.", ct);
                }
                return true;
            }
        }
        if (!request.Equals("/task retry", StringComparison.OrdinalIgnoreCase) && !IsWorkRetry(request)) return false;
        if (!current.Pending)
        {
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return true;
        }
        if (current.Google is { } google)
        {
            if (google.RetryAt is null) await ReplyToTaskAsync(current.Detail, ct);
            else await RunRequestedGoogleAsync(current, DateTimeOffset.UtcNow, ct, manual: true);
            return true;
        }
        if (BrowserPublicationTarget(current.Request) is { } retrySite)
        {
            await RunBrowserPublicationAsync(current, retrySite, ct);
            return true;
        }
        PrunePendingActions();
        if (pending.FirstOrDefault(item => item.Value.Work is not null) is { Value: not null } pendingWork)
        {
            await ReplyToTaskAsync($"The current task is waiting for /approve {pendingWork.Key} or /reject {pendingWork.Key}.", ct);
            return true;
        }
        if (resumableWork is { } run)
        {
            run.Frame = null;
            run.RetryEnd = run.Steps + run.Limit;
            run.Delegated = TakeTaskDelegation();
            await RunWorkAsync(run, ct);
        }
        else if (current.MayHaveChangedComputer)
            await ReplyToTaskAsync("The previous session may have changed the computer. Check the result and tell me the exact remaining step.", ct);
        else if (current.Execution is not null)
        {
            if (!await ExecuteReviewedTaskAsync(current, ct))
                await ReplyToTaskAsync("This task is still saved. It needs access to my writing folder and room in my personal task list.", ct);
        }
        else
        {
            var plan = await TurnPlanner.PlanAsync(current.Request, current.Speaker, false, config, [], model as Inference.ITurnContextModel,
                ct, allowWork: true, forceWork: true);
            await StartWorkAsync(current.Request, plan, ct, priorOverride: "");
        }
        return true;
    }

    private async Task<CurrentWorkflow> PreparePublicationNoteAsync(string request, string speaker, CancellationToken ct,
        string? previousDraft = null, int? preferredLength = null)
    {
        request = ClipWorkText(request, 7900);
        try
        {
            await life.LoadAsync(ct);
            CurrentWorkflow? first = null;
            foreach (var entry in life.State.Journal.Reverse())
            {
                if (entry.Goal is not (PersonalGoal.Explore or PersonalGoal.Company) || !WebParsing.IsWebUrl(entry.Reference) ||
                    PublicationText(entry.Observation) is not { } draft || draft == previousDraft) continue;
                var candidate = new CurrentWorkflow(request, speaker, WorkflowStatus.Draft,
                    $"I selected a saved reading note on {ClipWorkText(entry.Topic, 500)}.", Draft: draft);
                if (preferredLength is null || draft.Length <= preferredLength.Value) return candidate;
                first ??= candidate;
            }
            if (first is not null) return first;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { return new(request, speaker, WorkflowStatus.Blocked, "I couldn't read my saved diary. " + ClipWorkText(ex.Message, 500)); }
        return new(request, speaker, WorkflowStatus.Blocked, "I couldn't find a saved public-reading note to post.");
    }

    private static string? PublicationText(string text)
    {
        var body = NormalizeBrowserText(text).Trim();
        return body.Length > 0 && !body.Any(char.IsControl) ? body : null;
    }

}
