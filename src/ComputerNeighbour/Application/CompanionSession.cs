using ComputerNeighbour.ConsoleInterface;

namespace ComputerNeighbour.Application;

/// <summary>The console event loop switches sessions. Background input uses a snapshot of the active session.</summary>
internal sealed class CompanionSession(CompanionEngine regular, ICompanionOutput ui) : IAsyncDisposable
{
    internal sealed class View : IDisposable
    {
        private readonly CancellationTokenSource input = new();

        internal View(CompanionEngine engine)
        {
            Engine = engine;
            InputToken = input.Token;
        }

        internal CompanionEngine Engine { get; }
        internal CancellationToken InputToken { get; }
        internal void StopInput() => input.Cancel();
        public void Dispose() => input.Dispose();
    }

    private View current = new(regular);
    internal View Current => Volatile.Read(ref current);

    internal static bool IsIncognitoCommand(string text) =>
        text.Trim().ToLowerInvariant() is "/start incognito" or "/stop incognito";

    internal async Task<bool> TryHandleCommandAsync(string text, CancellationToken ct)
    {
        if (!IsIncognitoCommand(text)) return false;
        var start = text.Trim().Equals("/start incognito", StringComparison.OrdinalIgnoreCase);
        var previous = Current;
        if (start == previous.Engine.IsIncognito)
        {
            ui.Write(start ? "Incognito is already active." : "The regular session is already active.");
            return true;
        }

        previous.StopInput();
        try
        {
            await previous.Engine.WaitForAudioIdleAsync(ct);
            if (start)
            {
                var isolated = await regular.ForkIncognitoAsync(ct);
                regular.SetModelIncognito(true);
                Volatile.Write(ref current, new(isolated));
                ui.Write("Incognito started. This text session uses a temporary copy of your memory and relationship. Type /stop incognito to return.");
            }
            else
            {
                await previous.Engine.DisposeAsync();
                regular.SetModelIncognito(false);
                Volatile.Write(ref current, new(regular));
                ui.Write("Incognito ended. The experimental state was discarded and the regular session is active.");
            }
        }
        finally
        {
            // If a transition fails, let the still-current session accept microphone input again.
            if (ReferenceEquals(Current, previous)) Volatile.Write(ref current, new(previous.Engine));
            previous.Dispose();
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var active = Current;
        active.StopInput();
        if (active.Engine.IsIncognito) await active.Engine.DisposeAsync();
        active.Dispose();
    }
}
