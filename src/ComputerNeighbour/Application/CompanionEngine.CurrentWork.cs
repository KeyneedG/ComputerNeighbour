using System.Diagnostics;
using System.Text.RegularExpressions;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private const RegexOptions WorkWords = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private WorkRun? resumableWork;
    internal Action<Uri>? OpenXComposerOverride { get; init; }
    internal bool HasPriorityWork => !IsIncognito && memory.Tasks.Current?.Pending == true;

    internal async Task RestoreCurrentWorkAsync(CancellationToken ct)
    {
        if (IsIncognito) return;
        if (memory.Tasks.Current is { Status: WorkflowStatus.Running or WorkflowStatus.Approval } current)
        {
            await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.Blocked,
                Detail = "The session ended before I could finish. Any desktop change needs checking before another attempt." }, ct);
            return;
        }
        if (memory.Tasks.Current is not null) return;

        // Recover the earlier explicit X note request when this version first sees it.
        var recent = memory.Recent.Where(entry => entry.Role == "user" && IsMainSpeaker(entry.SpeakerId)).TakeLast(12).ToArray();
        var earlier = recent.LastOrDefault(entry => IsXNoteRequest(entry.Text));
        if (earlier is not null && recent.SkipWhile(entry => entry != earlier).Skip(1).All(entry => IsWorkRetry(entry.Text) || IsWorkStatus(entry.Text)))
            await PrepareXNoteAsync(earlier.Text, earlier.SpeakerId, ct);
    }

    private async Task SaveCurrentWorkAsync(CurrentWorkflow current, CancellationToken ct)
    {
        var state = memory.Tasks;
        if (!await memory.SaveTasksAsync(state, state with { Current = current }, ct))
            throw new InvalidOperationException("The current task changed. Check /task current.");
    }

    private async Task UpdateCurrentWorkAsync(WorkRun run, WorkflowStatus status, string detail, CancellationToken ct)
    {
        if (!ReferenceEquals(run, resumableWork) || memory.Tasks.Current is not { } current) return;
        await SaveCurrentWorkAsync(current with { Status = status, Detail = ClipWorkText(detail, 950) }, ct);
    }

    internal string DescribeCurrentWork() => memory.Tasks.Current is { } current
        ? $"Current task · {current.Request}\n{current.Status} · {current.Detail}" : "There is no current workflow.";

    private static bool IsXNoteRequest(string input)
    {
        var text = WorkRequest.WithoutQuotedText(input);
        return Regex.IsMatch(text, @"(?:^|\s)(?:https?://)?(?:www\.)?x\.com(?=$|[\s/?!,]|\.(?:\s|$))", WorkWords) &&
            Regex.IsMatch(text, @"^(?:(?:could|can|would|will) you\s+|please\s+|/work\s+)?(?:enter|go to|open|post|publish|зайди|открой|опубликуй)\b", WorkWords) &&
            Regex.IsMatch(text, @"\b(?:post|publish|опубликуй)\b.*\b(?:notes?|заметк\p{L}*)\b", WorkWords) &&
            !Regex.IsMatch(text, @"\b(?:don't|do not|never|не)\s+(?:post|publish|опубликуй)\b", WorkWords) &&
            WorkRequest.PermitsWebLookup(text);
    }

    private static bool IsWorkRetry(string input) => Regex.IsMatch(input.Trim(),
        @"^(?:(?:ok(?:ay)?|so|please)[,\s]+)*(?:(?:go on and )?try again|retry(?: the task)?|continue|resume(?: the task)?|go (?:on|ahead)|(?:can|could|would) (?:you )?(?:do it|try(?: it)? again)(?: now)?|продолжи|попробуй (?:снова|ещё раз)|(?:можешь )?сделай это сейчас)[.?!\s]*$", WorkWords);

    private static bool IsWorkStatus(string input) => Regex.IsMatch(input.Trim(),
        @"^(?:why (?:did )?you stop(?:ped)?|why did it stop|what happened(?: to (?:it|the task))?|are you (?:doing it|working on it)|but you're not doing anything|почему (?:ты )?остановил\p{L}*|что с задачей)[.?!\s]*$", WorkWords);

    private bool IsCurrentWorkTurn(string input) => !IsIncognito &&
        (input.Trim().Equals("/task current", StringComparison.OrdinalIgnoreCase) || IsXNoteRequest(input.Trim()) ||
         memory.Tasks.Current is { } current && IsMainSpeaker(current.Speaker) &&
         (IsWorkStatus(input) || current.Pending && IsWorkRetry(input) || input.Trim().Equals("/task retry", StringComparison.OrdinalIgnoreCase) ||
          current.Pending && IsXNoteRequest(current.Request) && (input.Trim().Equals("/task compose", StringComparison.OrdinalIgnoreCase) ||
              input.Trim().StartsWith("/task posted ", StringComparison.OrdinalIgnoreCase))));

    internal async Task RejectFromConsoleAsync(int id, CancellationToken ct)
    {
        if (pending.TryGetValue(id, out var item) && item.Work is { } run)
            await UpdateCurrentWorkAsync(run, WorkflowStatus.Blocked, "The proposed action was rejected. Use /task retry to review the next step.", ct);
        Reject(id);
    }

    private async Task<bool> TryCurrentWorkAsync(string input, CancellationToken ct)
    {
        if (IsIncognito) return false;
        var request = input.Trim();
        if (request.Equals("/task current", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return true;
        }
        if (IsXNoteRequest(request))
        {
            PrunePendingActions();
            if (pending.Values.Any(item => item.Work is not null))
                await ReplyToTaskAsync("A workflow is waiting for approval. Resolve it or use /cancel before starting a new one.", ct);
            else
            {
                if (memory.Tasks.Current is not { Pending: true, Draft: not null } active || !IsXNoteRequest(active.Request))
                    await PrepareXNoteAsync(request, currentSpeaker, ct);
                await ShowXDraftAsync(ct);
            }
            return true;
        }
        if (memory.Tasks.Current is not { } current || !IsMainSpeaker(current.Speaker)) return false;
        if (IsWorkStatus(request))
        {
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return true;
        }
        if (current.Pending && IsXNoteRequest(current.Request))
        {
            if (request.Equals("/task compose", StringComparison.OrdinalIgnoreCase))
            {
                await OpenXDraftAsync(current, ct);
                return true;
            }
            if (request.StartsWith("/task posted ", StringComparison.OrdinalIgnoreCase))
            {
                var link = request[13..].Trim();
                if (current.Status != WorkflowStatus.ComposerOpened || !ValidXPostLink(link))
                    await ReplyToTaskAsync("After posting, use /task posted followed by the exact X post URL.", ct);
                else
                {
                    await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.Finished, Detail = "You reported posting it at " + link }, ct);
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
        if (IsXNoteRequest(current.Request))
        {
            if (current.Draft is null) await PrepareXNoteAsync(current.Request, current.Speaker, ct);
            await ShowXDraftAsync(ct);
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
        else
        {
            var plan = await TurnPlanner.PlanAsync(current.Request, current.Speaker, false, config, [], model as Inference.ITurnContextModel,
                ct, allowWork: true, forceWork: true);
            await StartWorkAsync(current.Request, plan, ct, priorOverride: "");
        }
        return true;
    }

    private async Task PrepareXNoteAsync(string request, string speaker, CancellationToken ct)
    {
        DiaryEntry? note = null;
        try
        {
            await life.LoadAsync(ct);
            note = life.State.Journal.Reverse().FirstOrDefault(entry => entry.Goal is PersonalGoal.Explore or PersonalGoal.Company &&
                PersonalWebLink(entry.Reference) && CleanXExcerpt(entry.Observation) is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { ui.Write("[X draft] The saved diary could not be read. " + ex.Message); }
        var draft = note is null ? null : CleanXExcerpt(note.Observation);
        resumableWork = null;
        await SaveCurrentWorkAsync(new(ClipWorkText(request, 7900), speaker, draft is null ? WorkflowStatus.Blocked : WorkflowStatus.Draft,
            draft is null ? "I need a short saved public-reading note before I can prepare the post."
                : $"A short excerpt from my saved note on {note!.Topic} is ready for your review. Use /task compose to open it on X.", Draft: draft), ct);
    }

    private static string? CleanXExcerpt(string text)
    {
        var first = Regex.Split(text.Trim(), @"(?<=[.!?])\s+", RegexOptions.CultureInvariant)[0].Trim();
        return first.Length is > 15 and <= 140 && !first.Any(char.IsControl) && !first.Contains('@') &&
            !first.Contains("://", StringComparison.Ordinal) && !first.Contains('<') ? first : null;
    }

    private async Task ShowXDraftAsync(CancellationToken ct)
    {
        var current = memory.Tasks.Current!;
        if (current.Draft is { } draft) ui.Write("[X draft] " + draft);
        await ReplyToTaskAsync(current.Detail, ct);
    }

    private async Task OpenXDraftAsync(CurrentWorkflow current, CancellationToken ct)
    {
        if (!config.Permissions.OpenApplications || !config.Permissions.SearchWeb || current.Draft is not { } draft)
        {
            await ReplyToTaskAsync("Opening the reviewed draft on X needs browser launch, web access and a prepared draft.", ct);
            return;
        }
        ct.ThrowIfCancellationRequested();
        var composer = new Uri("https://x.com/intent/tweet?text=" + Uri.EscapeDataString(draft));
        try
        {
            if (OpenXComposerOverride is { } open) open(composer);
            else
            {
                using var launch = Process.Start(new ProcessStartInfo(composer.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.Blocked, Detail = "Windows could not open the X composer. " + ClipWorkText(ex.Message, 500) }, ct);
            await ReplyToTaskAsync(DescribeCurrentWork(), ct);
            return;
        }
        const string next = "I handed the draft to your browser. Check the account and select Post on X. Then use /task posted with the post URL.";
        await SaveCurrentWorkAsync(current with { Status = WorkflowStatus.ComposerOpened, Detail = next }, ct);
        await ReplyToTaskAsync(next, ct);
    }

    private static bool ValidXPostLink(string value) => value.Length <= 240 &&
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort &&
        url.UserInfo.Length == 0 && url.Host is ("x.com" or "www.x.com") &&
        Regex.IsMatch(url.AbsolutePath, @"^/[a-zA-Z0-9_]{1,15}/status/[0-9]+/?$");
}
