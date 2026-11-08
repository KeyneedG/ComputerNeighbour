namespace ComputerNeighbour.Personality;

public sealed record ReactionUpdate(string[]? Feelings, string? Detail);

internal sealed record RecentReaction(string Feelings, string Detail, DateTimeOffset At, long Interaction)
{
    internal static readonly IReadOnlyList<string> Labels = Array.AsReadOnly(new[]
    {
        "afraid", "proud", "relieved", "awkward", "nostalgic", "eager", "frustrated", "regretful", "surprised",
        "grateful", "excited", "fond", "indignant", "impressed", "content", "uncertain", "envious", "empathetic"
    });

    internal bool IsFresh(DateTimeOffset now, long interactions) =>
        now >= At && now - At < TimeSpan.FromMinutes(30) && interactions - Interaction < 4;

    internal static RecentReaction? From(ReactionUpdate? update, string? userText, string? answer, DateTimeOffset now, long interaction)
    {
        if (update?.Feelings is not { Length: 1 or 2 } feelings ||
            feelings.Any(label => !Labels.Contains(label, StringComparer.Ordinal)) ||
            feelings.Distinct(StringComparer.Ordinal).Count() != feelings.Length ||
            update.Detail?.Trim() is not { Length: >= 6 and <= 120 } detail ||
            detail.Any(char.IsControl) || !detail.Any(char.IsLetterOrDigit) ||
            (userText?.Contains(detail, StringComparison.Ordinal) != true && answer?.Contains(detail, StringComparison.Ordinal) != true))
            return null;
        return new(string.Join(" + ", feelings), detail, now, interaction);
    }
}
