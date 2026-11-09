using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

internal sealed class PersonalFileConflict(string message) : IOException(message);

internal sealed class PersonalWriting(PortablePaths paths, FileWorkspace workspace)
{
    internal const string Folder = "documents/evelyn-writing";
    private const string SectionBreak = "\n\n---\n\n";
    private static readonly Regex LegacyName = new("^[0-9a-f]{32}\\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Device = new(@"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase);

    internal static string Title(string text, string fallback)
    {
        var first = text.Trim().Split('\n', 2)[0].Trim();
        var clean = new string(first.Take(78).Where(ch => !char.IsControl(ch) && !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '#', '"', '\'', '`', '*', '_');
        if (clean.Length < 3 || !clean.Any(char.IsLetter) || Device.IsMatch(clean))
            return fallback;
        return clean;
    }

    internal string? NextLegacyFile(IEnumerable<string> excluded)
    {
        var directory = Path.GetDirectoryName(paths.Inside(Folder + "/probe.md"))!;
        if (!Directory.Exists(directory)) return null;
        var skip = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DirectoryInfo(directory).EnumerateFiles("*.md", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
        }).Where(file => LegacyName.IsMatch(file.Name) && file.Length is > 0 and <= 1_000_000)
          .Select(file => Folder + "/" + file.Name).Where(path => !skip.Contains(path)).Order(StringComparer.OrdinalIgnoreCase)
          .FirstOrDefault();
    }

    internal static bool IsPersonalWriting(string path) =>
        path.Replace('\\', '/').StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase);

    internal string Reserve(string title)
    {
        for (var number = 1; number <= 500; number++)
        {
            var suffix = number == 1 ? "" : $" ({number})";
            var candidate = Folder + "/" + title + suffix + ".md";
            var full = FullPath(candidate);
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
        throw new IOException("All available names for this title are in use.");
    }

    internal Task<string> ReadAsync(string path, CancellationToken ct)
    {
        FullPath(path);
        return workspace.ReadForRevisionAsync(path, ct);
    }

    internal string? FindForPublication(string reference, DateTimeOffset? offeredAt)
    {
        var directory = Path.GetDirectoryName(paths.Inside(Folder + "/probe.md"))!;
        if (!Directory.Exists(directory)) return null;
        var files = new DirectoryInfo(directory).EnumerateFiles("*.md", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
        }).Where(file => file.Length is > 0 and <= 1_000_000 && file.Name.Length <= 110 && !Device.IsMatch(file.Name))
          .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var named = files.Where(file => offeredAt is null || file.CreationTimeUtc <= offeredAt.Value.UtcDateTime)
            .Where(file => Path.GetFileNameWithoutExtension(file.Name) is { Length: >= 5 } title &&
                reference.Contains(title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Name.Length).FirstOrDefault();
        var selected = named ?? files.FirstOrDefault(file => offeredAt is null || file.LastWriteTimeUtc <= offeredAt.Value.UtcDateTime);
        return selected is null ? null : Folder + "/" + selected.Name;
    }

    internal static string StoryText(string title, string[] scenes, bool draft)
    {
        var heading = "# " + title + "\n\n";
        if (draft) heading += $"> Draft · {scenes.Length}/3 scenes\n\n";
        return heading + string.Join(SectionBreak, scenes) + "\n";
    }

    internal async Task<string[]> RereadDraftAsync(string path, string expectedHash, int sceneCount, CancellationToken ct)
    {
        var text = await ReadAsync(path, ct);
        if (HashText(text) != expectedHash) throw new PersonalFileConflict("The saved draft changed outside this project.");
        var blocks = text.ReplaceLineEndings("\n").Split("\n\n", 3, StringSplitOptions.None);
        if (blocks.Length != 3 || !blocks[1].StartsWith("> Draft · ", StringComparison.Ordinal))
            throw new InvalidDataException("The saved draft could not be read as a story.");
        var scenes = blocks[2].TrimEnd('\n').Split(SectionBreak, StringSplitOptions.None);
        if (scenes.Length != sceneCount) throw new InvalidDataException("The saved draft has an unexpected number of scenes.");
        return scenes;
    }

    internal async Task PublishAsync(string path, string text, string? originalHash, CancellationToken ct)
    {
        var full = FullPath(path);
        if (!File.Exists(full))
        {
            if (originalHash is not null) throw new PersonalFileConflict("The published story was moved or removed.");
            await workspace.ApplyVerifiedAsync(await workspace.PrepareCreateAsync(path, text, ct), ct);
            return;
        }
        var current = await ReadAsync(path, ct);
        if (current.ReplaceLineEndings("\n") == text.ReplaceLineEndings("\n")) return;
        if (originalHash is null || HashText(current) != originalHash)
            throw new PersonalFileConflict("The destination changed since it was last read.");
        var change = await workspace.PrepareEditAsync(path, current, text, ct);
        if (change.BeforeText is null || HashText(change.BeforeText) != originalHash)
            throw new PersonalFileConflict("The story changed while I was preparing the edit.");
        await workspace.ApplyVerifiedAsync(change, ct);
    }

    internal static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal async Task<string> LegacyHashAsync(string source, CancellationToken ct)
    {
        var full = FullPath(source);
        if (!LegacyName.IsMatch(Path.GetFileName(full))) throw new UnauthorizedAccessException("Only original hex-named personal entries are migrated.");
        return await FileHashAsync(full, ct);
    }

    internal async Task RenameLegacyAsync(string source, string destination, string expected, CancellationToken ct)
    {
        var from = FullPath(source);
        var to = FullPath(destination);
        if (!LegacyName.IsMatch(Path.GetFileName(from))) throw new UnauthorizedAccessException("This is not an original personal entry.");
        if (!File.Exists(from) && File.Exists(to) && await FileHashAsync(to, ct) == expected) return;
        if (File.Exists(to) || Directory.Exists(to) || await FileHashAsync(from, ct) != expected)
            throw new PersonalFileConflict("The old entry or its new name changed.");
        ct.ThrowIfCancellationRequested();
        File.Move(FullPath(source), FullPath(destination), overwrite: false);
        if (await FileHashAsync(to, CancellationToken.None) == expected) return;
        File.Move(to, from, overwrite: false);
        throw new PersonalFileConflict("The old entry changed during the move.");
    }

    private string FullPath(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length != 3 || !IsPersonalWriting(relative) || !parts[2].EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            parts[2].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || parts[2].Length > 110 || Device.IsMatch(parts[2]))
            throw new UnauthorizedAccessException("Expected a Markdown file in Evelyn's own writing folder.");
        return paths.Inside(relative);
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > 1_000_000) throw new IOException("The personal entry exceeds its size limit.");
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }
}
