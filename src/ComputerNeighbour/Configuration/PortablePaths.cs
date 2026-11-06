namespace ComputerNeighbour.Configuration;

public sealed class PortablePaths
{
    public string Root { get; }
    public PortablePaths(string root) { Root = Path.GetFullPath(root); }
    public string Inside(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Use a relative path inside ComputerNeighbour: " + relative);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path leaves portable directory: " + relative);
        // Junctions/symlinks must not redirect app-owned data outside the portable root.
        for (var current = new DirectoryInfo(Path.GetDirectoryName(full)!); current is not null && current.FullName.Length >= Root.Length; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Portable data paths cannot use junctions/symlinks: " + relative);
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Portable file cannot be a symlink: " + relative);
        return full;
    }
    public void InitializeEnvironment()
    {
        foreach (var dir in new[] { "data/tmp", "data/tmp/audio", "data/logs", "data/cache", "data/memory", "data/personality", "data/speakers", "documents", "characters" }) Directory.CreateDirectory(Inside(dir));
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(name, Inside("data/tmp"));
        foreach (var name in new[] { "HF_HOME", "XDG_CACHE_HOME", "LLAMA_CACHE" }) Environment.SetEnvironmentVariable(name, Inside("data/cache"));
    }
    public static PortablePaths Discover(string[] args)
    {
        var index = Array.IndexOf(args, "--root");
        if (index >= 0) return new PortablePaths(index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("--root requires a folder"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "config")) || File.Exists(Path.Combine(dir.FullName, "NuGet.Config"))) return new PortablePaths(dir.FullName);
        return new PortablePaths(Environment.CurrentDirectory);
    }
}
