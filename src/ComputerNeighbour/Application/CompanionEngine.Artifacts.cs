using System.Text.RegularExpressions;
using ComputerNeighbour.Memory;

namespace ComputerNeighbour.Application;

public sealed partial class CompanionEngine
{
    private async Task<WorkArtifact?> GetWorkArtifactAsync(CancellationToken ct)
    {
        if (memory.LastArtifact is {} recorded) return IsMainSpeaker(recorded.SpeakerId) ? recorded : null;

        // Older builds kept the actual sketch receipt and its successful step separately.
        MemoryEntry? previousReceipt = null;
        foreach (var entry in memory.Recent.TakeLast(18).Reverse())
        {
            if (entry.Role == "assistant" && Regex.IsMatch(entry.Text, "^Saved and verified “"))
                previousReceipt = entry;
            else if (entry.Role == "user" && previousReceipt is {} receipt)
            {
                previousReceipt = null;
                if (!IsMainSpeaker(entry.SpeakerId)) continue;
                var match = Regex.Match(receipt.Text, """^Saved and verified “[^”\r\n]+” at (?<path>[^\r\n]+?\.png)\.""");
                if (!match.Success) continue;
                try
                {
                    var path = SketchActivity.ResolveSketchPath(paths, match.Groups["path"].Value);
                    if (File.Exists(path) && await thoughtJournal.CompletedSketchAsync(entry.At, receipt.At, ct))
                        return new(WorkArtifactKind.Image, path, ClipWorkText(entry.Text, 1500), entry.SpeakerId,
                            receipt.Text.Contains("I verified that Paint opened the PNG.", StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { }
            }
        }
        return null;
    }

    private async Task<string> ReadEditorArtifactAsync(WorkArtifact artifact,CancellationToken ct)
    {
        if (!config.Permissions.ReadFiles) throw new UnauthorizedAccessException("Reading the prior editor document is disabled.");
        var relative=Path.GetRelativePath(paths.Root,artifact.Path).Replace('\\','/');
        if (!Regex.IsMatch(relative,@"^data/tmp/editor-[a-f0-9]{32}\.txt$",RegexOptions.IgnoreCase))
            throw new UnauthorizedAccessException("The prior editor document is outside the application's document folder.");
        var path=paths.Inside(relative);
        await using var file=File.OpenRead(path);
        if (file.Length>100_000) throw new IOException("The prior editor document exceeds the revision size limit.");
        using var reader=new StreamReader(file);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task RecordArtifactAsync(WorkRun run, WorkArtifactKind kind, string path, bool openInPaint = false)
    {
        var goal = run.Request.Revision?.Goal is {} original
            ? ClipWorkText(original, 1000) + "\nLatest change: " + ClipWorkText(run.Request.Goal, 450)
            : ClipWorkText(run.Request.Goal, 1500);
        await SaveArtifactReceiptAsync(new(kind, path, goal, run.Speaker, openInPaint));
    }

    private async Task SaveArtifactReceiptAsync(WorkArtifact artifact)
    {
        try { await memory.SaveArtifactAsync(artifact, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ui.Write("[Work] The result was produced, but its follow-up record could not be saved. " + ex.Message); }
    }
}
