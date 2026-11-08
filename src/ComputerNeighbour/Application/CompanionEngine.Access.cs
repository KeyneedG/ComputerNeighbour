using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private enum AccessMode { Configured, Ask, Full }
    private AccessMode accessMode;
    private bool? desktopInputOverride;
    private bool delegateNextWork;

    private bool WorkDesktopEnabled => desktopInputOverride ?? config.Permissions.DesktopInput;

    internal string AccessStatus =>
        $"Computer access — {accessMode.ToString().ToLowerInvariant()}; guided desktop input {(WorkDesktopEnabled ? "enabled" : "disabled")}; " +
        $"one-task approval {(delegateNextWork ? "armed" : "off")}.";

    private static string AccessCommand(string text) => text.Trim().ToLowerInvariant();

    private static bool IsAccessCommand(string text) => AccessCommand(text) is
        "/access" or "/access default" or "/full access" or "/ask before edit" or "/approve for me" or
        "/desktop on" or "/desktop off" or "/cancel";

    internal async Task<bool> TryHandleAccessCommandAsync(string text, CancellationToken ct)
    {
        switch (AccessCommand(text))
        {
            case "/access":
                ui.Write(AccessStatus);
                return true;
            case "/full access":
                accessMode = AccessMode.Full;
                desktopInputOverride = true;
                delegateNextWork = false;
                ui.Write("Full access for this session. Explicit tasks may use allowed files and apps and the guided desktop. Automatic desktop input applies to configured applications.");
                return true;
            case "/ask before edit":
                accessMode = AccessMode.Ask;
                RevokeTaskDelegation();
                ui.Write("I will ask before file changes, app launches and desktop input. " + AccessStatus);
                return true;
            case "/access default":
                accessMode = AccessMode.Configured;
                desktopInputOverride = null;
                RevokeTaskDelegation();
                ui.Write("Restored access from the configuration. " + AccessStatus);
                return true;
            case "/desktop on":
                desktopInputOverride = true;
                ui.Write("Guided desktop input enabled for this session. " + AccessStatus);
                return true;
            case "/desktop off":
                desktopInputOverride = false;
                ui.Write("Guided desktop input disabled for this session.");
                return true;
            case "/cancel":
                InterruptActivity();
                foreach (var id in pending.Where(p => p.Value.Work is not null).Select(p => p.Key).ToArray())
                    pending.Remove(id);
                delegateNextWork = false;
                ui.Write("Stopped the current workflow and cleared its pending approval.");
                return true;
            case "/approve for me":
                await DelegateApprovalAsync(ct);
                return true;
            default:
                return false;
        }
    }

    private void RevokeTaskDelegation()
    {
        delegateNextWork = false;
        foreach (var item in pending.Values)
            if (item.Work is {} work) work.Delegated = false;
    }

    private async Task DelegateApprovalAsync(CancellationToken ct)
    {
        PrunePendingActions();
        var suspended = pending.FirstOrDefault(p => p.Value.Work is not null);
        if (suspended.Key > 0)
        {
            suspended.Value.Work!.Delegated = true;
            await ApproveAsync(suspended.Key, ct);
            return;
        }
        var actions = pending.Where(p => p.Value.CanDelegate).Select(p => p.Key).ToArray();
        if (actions.Length == 1)
        {
            await ApproveAsync(actions[0], ct);
            return;
        }
        if (actions.Length > 1)
        {
            ui.Write("Several actions are waiting. Use /approve with the number of the one you want.");
            return;
        }
        delegateNextWork = true;
        ui.Write("Automatic approval armed for your next explicit task.");
    }

    private bool TakeTaskDelegation()
    {
        var result = delegateNextWork;
        delegateNextWork = false;
        return result;
    }

    private bool RequiresSessionApproval(ToolRequest request, bool autoApprove = false) =>
        tools.ChangesComputer(request) && !autoApprove && (accessMode == AccessMode.Ask || tools.RequiresApproval(request));
}
