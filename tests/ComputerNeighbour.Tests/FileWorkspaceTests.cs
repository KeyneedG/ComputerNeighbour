using System.Text;
using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class FileWorkspaceTests
{
    private static FileWorkspace Workspace(string root, int maximum = 1_000_000) => new(new PortablePaths(root), ["documents"], maximum);
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("file creation is staged without writing the target and commits literal Unicode atomically", async root =>
        {
            var workspace = Workspace(root);
            var prepared = await workspace.PrepareCreateAsync("documents/sub/new.md", "Hello, мир!\n", CancellationToken.None);
            Check.True(!File.Exists(Path.Combine(root, "documents", "sub", "new.md")), "Preparation wrote the target before approval.");
            Check.Equal("create_file", prepared.Tool); Check.Equal("", prepared.BeforeSha256);
            Check.True(prepared.Preview.Contains("+Hello, мир!"), "The preview did not show new content.");
            var result = await workspace.ApplyAsync(prepared, CancellationToken.None);
            Check.True(result.StartsWith("Created documents/sub/new.md"), "The creation result was not concrete.");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "documents", "sub", "new.md"));
            Check.Equal("Hello, мир!\n", Encoding.UTF8.GetString(bytes));
            Check.True(!bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "New files unexpectedly received a BOM.");
        });
        await suite.Case("file edits require an exact unique passage and preserve surrounding text and CRLF", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents")); var file = Path.Combine(root, "documents", "note.txt");
            await File.WriteAllTextAsync(file, "First\r\nReplace this\r\nLast\r\n", new UTF8Encoding(false));
            var workspace = Workspace(root);
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareEditAsync("documents/note.txt", "missing", "new"));
            await Check.ThrowsAsync<ArgumentException>(() => workspace.PrepareEditAsync("documents/note.txt", "", "new"));
            var prepared = await workspace.PrepareEditAsync("documents/note.txt", "Replace this", "New\nSecond");
            Check.True(prepared.Preview.Contains("-Replace this") && prepared.Preview.Contains("+New"), "The preview omitted the diff.");
            await workspace.ApplyAsync(prepared);
            Check.Equal("First\r\nNew\r\nSecond\r\nLast\r\n", await File.ReadAllTextAsync(file));
            Check.True(!Directory.GetFiles(Path.Combine(root, "documents"), ".neighbour-*.tmp").Any(), "Commit left a temporary file.");
        });
        await suite.Case("file edit rejects repeated and overlapping passages and no-op changes", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents")); var file = Path.Combine(root, "documents", "note.txt");
            await File.WriteAllTextAsync(file, "aaa repeated repeated"); var workspace = Workspace(root);
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareEditAsync("documents/note.txt", "repeated", "new"));
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareEditAsync("documents/note.txt", "aa", "new"));
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareEditAsync("documents/note.txt", "aaa", "aaa"));
        });
        await suite.Case("creation collisions and stale edit approvals never overwrite current data", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents")); var file = Path.Combine(root, "documents", "note.md"); var workspace = Workspace(root);
            var create = await workspace.PrepareCreateAsync("documents/note.md", "first");
            await File.WriteAllTextAsync(file, "external version");
            await Check.ThrowsAsync<IOException>(() => workspace.ApplyAsync(create));
            await Check.ThrowsAsync<IOException>(() => workspace.PrepareCreateAsync("documents/note.md", "overwrite"));
            var edit = await workspace.PrepareEditAsync("documents/note.md", "external", "edited");
            await File.WriteAllTextAsync(file, "newer external version");
            await Check.ThrowsAsync<IOException>(() => workspace.ApplyAsync(edit));
            Check.Equal("newer external version", await File.ReadAllTextAsync(file));
        });
        await suite.Case("UTF-8 BOM and little/big endian UTF-16 encodings survive edits with byte-exact backups", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents")); var workspace = Workspace(root);
            Encoding[] encodings = [new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];
            for (var i = 0; i < encodings.Length; i++)
            {
                var relative = $"documents/encoding-{i}.txt"; var file = Path.Combine(root, "documents", $"encoding-{i}.txt");
                await File.WriteAllTextAsync(file, "Before мир\r\n", encodings[i]); var original = await File.ReadAllBytesAsync(file);
                var change = await workspace.PrepareEditAsync(relative, "Before", "After"); await workspace.ApplyAsync(change);
                var actual = await File.ReadAllBytesAsync(file); var expected = encodings[i].GetPreamble().Concat(encodings[i].GetBytes("After мир\r\n")).ToArray();
                Check.True(actual.SequenceEqual(expected), "The original encoding/BOM was changed.");
                var metadataFiles = Directory.GetFiles(Path.Combine(root, "data", "file-backups"), "metadata.json", SearchOption.AllDirectories);
                var metadata = metadataFiles.Single(m => JsonDocument.Parse(File.ReadAllText(m)).RootElement.GetProperty("relativePath").GetString() == relative);
                var backup = Path.Combine(Path.GetDirectoryName(metadata)!, "before.bin");
                Check.True((await File.ReadAllBytesAsync(backup)).SequenceEqual(original), "Backup differs from the original file bytes.");
            }
        });
        await suite.Case("write scopes reject traversal, ADS, reserved devices, scripts and app internals", async root =>
        {
            var workspace = Workspace(root);
            foreach (var path in new[] { "../escape.txt", "documents/../escape.txt", "documents/note.txt:stream", "documents/CON.txt", "documents/NUL.md", "documents/COM1.txt", "documents/LPT¹.txt", "documents/trailing./note.txt", "documents/note.ps1", "documents/app.exe", "config/appsettings.json", "src/notes.txt", "characters/test.md", ".git/config.txt", "outside/note.md", "root.md" })
            {
                try { await workspace.PrepareCreateAsync(path, "never"); throw new InvalidOperationException("Path was writable: " + path); }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { }
            }
            foreach (var directory in new[] { ".", "data", "config", "src", "runtimes", "../outside" })
            {
                try { _ = new FileWorkspace(new(root), [directory]); throw new InvalidOperationException("Protected scope was accepted: " + directory); }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { }
            }
        });
        await suite.Case("writer rejects binary/invalid text, oversized content and tampered snapshots", async root =>
        {
            var workspace = Workspace(root, 100); Directory.CreateDirectory(Path.Combine(root, "documents"));
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareCreateAsync("documents/binary.txt", "nul\0data"));
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareCreateAsync("documents/invalid.txt", "\uD800"));
            await Check.ThrowsAsync<IOException>(() => workspace.PrepareCreateAsync("documents/big.txt", new string('x', 101)));
            await File.WriteAllBytesAsync(Path.Combine(root, "documents", "binary.txt"), [0xFF, 0x01, 0x02]);
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.PrepareEditAsync("documents/binary.txt", "data", "new"));
            await File.WriteAllTextAsync(Path.Combine(root, "documents", "note.txt"), "before");
            var edit = await workspace.PrepareEditAsync("documents/note.txt", "before", "after");
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.ApplyAsync(edit with { BeforeText = "different" }));
            await Check.ThrowsAsync<InvalidDataException>(() => workspace.ApplyAsync(edit with { Tool = "run_shell" }));
            Check.Equal("before", await File.ReadAllTextAsync(Path.Combine(root, "documents", "note.txt")));
        });
        await suite.Case("cancelled writes leave the target intact and previews disclose clipping", async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "documents")); var file = Path.Combine(root, "documents", "note.txt");
            await File.WriteAllTextAsync(file, "original"); var workspace = Workspace(root);
            var edit = await workspace.PrepareEditAsync("documents/note.txt", "original", "changed");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => workspace.ApplyAsync(edit, cancelled.Token));
            Check.Equal("original", await File.ReadAllTextAsync(file));
            var large = await workspace.PrepareCreateAsync("documents/large.md", new string('x', 9000));
            Check.True(large.Preview.Contains("Preview truncated") && large.Preview.Length < 6200, "A clipped preview was not clearly marked.");
        });
    }
}
