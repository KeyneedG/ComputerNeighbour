using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Tools;

public sealed record PreparedFileChange(string Tool, string RelativePath, string BeforeSha256, string? BeforeText, string AfterText, string Preview);

/// <summary>Prepares reviewable text changes, then commits them within explicitly configured portable scopes.</summary>
public sealed class FileWorkspace
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".json", ".csv", ".tsv", ".html", ".htm", ".css", ".xml", ".yaml", ".yml", ".ini" };
    private static readonly HashSet<string> ProtectedRoots = new(StringComparer.OrdinalIgnoreCase)
        { "app", "config", "characters", "src", "tests", "scripts", "models", "runtimes", "data" };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly PortablePaths paths;
    private readonly string[] writableDirectories;
    private readonly int maxFileBytes;
    private static readonly SemaphoreSlim CommitGate = new(1, 1);

    public FileWorkspace(PortablePaths paths, IEnumerable<string> writableDirectories, int maxFileBytes = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(writableDirectories);
        if (maxFileBytes is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        this.paths = paths; this.maxFileBytes = maxFileBytes;
        this.writableDirectories = writableDirectories.Select(ValidateDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PreparedFileChange> PrepareCreateAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = ResolveWritable(path);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("Creation requires a new file; the path already exists.");
        ValidateText(content); EnsureSize(Utf8.GetByteCount(content));
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var relative = Relative(full);
        return new("create_file", relative, "", null, content, Preview(relative, null, content));
    }

    public async Task<PreparedFileChange> PrepareEditAsync(string path, string oldText, string newText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldText); ArgumentNullException.ThrowIfNull(newText);
        if (oldText.Length == 0) throw new ArgumentException("oldText must identify a nonempty, exact, unique passage.", nameof(oldText));
        var full = ResolveWritable(path);
        var bytes = await ReadBoundedAsync(full, cancellationToken);
        var decoded = Decode(bytes);
        ValidateText(oldText); ValidateText(newText);
        var position = decoded.Text.IndexOf(oldText, StringComparison.Ordinal);
        if (position < 0) throw new InvalidDataException("The exact oldText passage was not found; read the current file first.");
        if (decoded.Text.IndexOf(oldText, position + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("oldText occurs more than once; include enough surrounding text to identify one passage.");
        newText = NormalizeNewlines(newText, DetectNewline(decoded.Text));
        var after = decoded.Text[..position] + newText + decoded.Text[(position + oldText.Length)..];
        if (after == decoded.Text) throw new InvalidDataException("The proposed edit makes no change.");
        ValidateText(after); EnsureSize(decoded.Encoding.GetByteCount(after) + decoded.Preamble.Length);
        var relative = Relative(full);
        return new("edit_file", relative, Hash(bytes), decoded.Text, after, Preview(relative, decoded.Text, after));
    }

    public Task<string> ApplyAsync(PreparedFileChange change, CancellationToken cancellationToken = default) =>
        ApplyAsync(change, verify: false, cancellationToken);

    internal Task<string> ApplyVerifiedAsync(PreparedFileChange change, CancellationToken cancellationToken) =>
        ApplyAsync(change, verify: true, cancellationToken);

    private async Task<string> ApplyAsync(PreparedFileChange change, bool verify, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Tool is not ("create_file" or "edit_file")) throw new InvalidDataException("Unknown prepared file operation.");
        ArgumentNullException.ThrowIfNull(change.AfterText);
        var full = ResolveWritable(change.RelativePath);
        ValidateText(change.AfterText);
        await CommitGate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            full = ResolveWritable(change.RelativePath); // Recheck scopes and junctions at approval time.
            byte[]? beforeBytes = null;
            var encoding = new DecodedText("", Utf8, []);
            if (change.Tool == "create_file")
            {
                if (change.BeforeText is not null || change.BeforeSha256 != "") throw new InvalidDataException("Invalid creation snapshot.");
                if (File.Exists(full) || Directory.Exists(full)) throw new IOException("The target was created after this proposal. Nothing was overwritten.");
            }
            else
            {
                beforeBytes = await ReadBoundedAsync(full, cancellationToken);
                if (!string.Equals(Hash(beforeBytes), change.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The file changed after this proposal. Prepare and review a new edit; nothing was overwritten.");
                encoding = Decode(beforeBytes);
                if (change.BeforeText != encoding.Text) throw new InvalidDataException("The prepared original text does not match its file snapshot.");
                if (encoding.Text == change.AfterText) throw new InvalidDataException("The proposed edit makes no change.");
            }
            var payload = Encode(change.AfterText, encoding.Encoding, encoding.Preamble);
            EnsureSize(payload.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ResolveWritable(change.RelativePath);
            temporary = Path.Combine(Path.GetDirectoryName(full)!, ".neighbour-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            string? backupRelative = null;
            if (beforeBytes is not null)
            {
                var changeId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                var directory = "data/file-backups/" + changeId;
                backupRelative = directory + "/before.bin";
                var backup = paths.Inside(backupRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                await WriteDurableNewAsync(backup, beforeBytes, cancellationToken);
                var metadata = new
                {
                    relativePath = change.RelativePath, tool = change.Tool, beforeSha256 = change.BeforeSha256,
                    afterSha256 = Hash(payload), encoding = encoding.Encoding.WebName,
                    preambleHex = Convert.ToHexString(encoding.Preamble), preparedBackupAt = DateTimeOffset.UtcNow,
                    restore = "Copy before.bin back to relativePath inside the portable root to restore the original bytes."
                };
                await WriteDurableNewAsync(paths.Inside(directory + "/metadata.json"), Utf8.GetBytes(JsonSerializer.Serialize(metadata, AppConfig.Json)), cancellationToken);
                // Optimistic concurrency: a stale reviewed proposal must never knowingly overwrite newer data.
                var current = await ReadBoundedAsync(full, cancellationToken);
                if (!string.Equals(Hash(current), change.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The file changed while this edit was being committed. The proposed edit was not applied.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ResolveWritable(change.RelativePath);
            if (change.Tool == "create_file") File.Move(temporary, full, overwrite: false);
            else File.Replace(temporary, full, destinationBackupFileName: null);
            temporary = null;
            var result = change.Tool == "create_file" ? "Created " + change.RelativePath + "." : "Edited " + change.RelativePath + ". Original bytes backed up to " + backupRelative + ".";
            if (!verify) return result;
            try
            {
                // Finish the read-back once the atomic commit has happened, even if the workflow was interrupted.
                var written = await ReadBoundedAsync(ResolveWritable(change.RelativePath), CancellationToken.None);
                if (!written.AsSpan().SequenceEqual(payload)) throw new IOException("The saved bytes differ from the committed text.");
                return result + " Verified the saved bytes.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { throw new IOException(result + " Verification failed. " + ex.Message, ex); }
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            CommitGate.Release();
        }
    }

    private string ValidateDirectory(string relative)
    {
        var parts = ValidateSegments(relative);
        if (parts.Length == 0 || IsProtected(parts[0])) throw new UnauthorizedAccessException("Writable directories cannot include app internals or the portable root.");
        return paths.Inside(string.Join(Path.DirectorySeparatorChar, parts)).TrimEnd(Path.DirectorySeparatorChar);
    }
    private string ResolveWritable(string relative)
    {
        var parts = ValidateSegments(relative);
        if (parts.Length < 2 || IsProtected(parts[0])) throw new UnauthorizedAccessException("App internals and root files are not a writable workspace.");
        var full = paths.Inside(string.Join(Path.DirectorySeparatorChar, parts));
        if (!writableDirectories.Any(d => full.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("The file is outside permissions.writableDirectories.");
        if (!Extensions.Contains(Path.GetExtension(full))) throw new UnauthorizedAccessException("Only approved plain text formats can be written; executable and script formats are disabled.");
        if (Directory.Exists(full)) throw new IOException("The target is a directory.");
        return full;
    }
    private static bool IsProtected(string first) => first.StartsWith('.') || ProtectedRoots.Contains(first);
    private static string[] ValidateSegments(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Use a relative workspace path without a drive or alternate data stream.");
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c)))
                throw new InvalidDataException("Invalid or ambiguous path component.");
            if (Regex.IsMatch(part, @"^(?:CON|PRN|AUX|NUL|CLOCK\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Windows reserved device names cannot be files.");
        }
        return parts;
    }
    private string Relative(string full) => Path.GetRelativePath(paths.Root, full).Replace('\\', '/');
    private void EnsureSize(int bytes) { if (bytes > maxFileBytes) throw new IOException($"File exceeds the writable limit of {maxFileBytes} bytes."); }
    private async Task<byte[]> ReadBoundedAsync(string full, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > maxFileBytes) throw new IOException($"File exceeds the writable limit of {maxFileBytes} bytes.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static void ValidateText(string text)
    {
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) throw new InvalidDataException("Binary/control data is not an editable text file.");
        try { Utf8.GetByteCount(text); } catch (EncoderFallbackException ex) { throw new InvalidDataException("Text contains invalid Unicode.", ex); }
    }
    private static DecodedText Decode(byte[] bytes)
    {
        Encoding encoding = Utf8; var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw new InvalidDataException("UTF-32 files are not supported; use UTF-8 or UTF-16.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            ValidateText(text);
            return new(text, encoding, bytes[..skip]);
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("The existing file is not valid UTF-8/UTF-16 text.", ex); }
    }
    private static byte[] Encode(string text, Encoding encoding, byte[] preamble)
    {
        var body = encoding.GetBytes(text); var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0); body.CopyTo(result, preamble.Length); return result;
    }
    private static async Task WriteDurableNewAsync(string full, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string DetectNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') && !text.Contains('\n') ? "\r" : "\n";
    private static string NormalizeNewlines(string text, string newline) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);
    private sealed record DecodedText(string Text, Encoding Encoding, byte[] Preamble);

    private static string Preview(string path, string? before, string after)
    {
        var oldLines = before is null ? [] : Regex.Split(before, "\r\n|\n|\r");
        var newLines = Regex.Split(after, "\r\n|\n|\r");
        var prefix = 0; while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0; while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var start = Math.Max(0, prefix - 3); var oldEnd = oldLines.Length - suffix; var newEnd = newLines.Length - suffix;
        var tail = Math.Min(3, suffix);
        var output = new StringBuilder($"--- {(before is null ? "/dev/null" : path)}\n+++ {path}\n@@ -{start + 1},{oldEnd - start + tail} +{start + 1},{newEnd - start + tail} @@\n");
        void Add(char marker, string line) { if (output.Length < 6200) output.Append(marker).AppendLine(line); }
        for (var i = start; i < prefix; i++) Add(' ', oldLines[i]);
        for (var i = prefix; i < oldEnd; i++) Add('-', oldLines[i]);
        for (var i = prefix; i < newEnd; i++) Add('+', newLines[i]);
        for (var i = 0; i < tail; i++) Add(' ', newLines[newEnd + i]);
        const int maximum = 6000;
        return output.Length <= maximum ? output.ToString() : output.ToString(0, maximum) + "\n[Preview truncated. Review the complete staged proposal before approval.]";
    }
}
