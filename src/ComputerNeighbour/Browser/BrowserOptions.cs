namespace ComputerNeighbour.Browser;

public sealed class BrowserOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxCharacters { get; set; } = 16000;
    public int ReadTimeoutSeconds { get; set; } = 8;
    public List<string> AllowedProcesses { get; set; } = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];
}

public sealed record BrowserPage(string Browser, string Title, string? Url, string Text, bool Truncated);

public interface IBrowserReader
{
    Task<BrowserPage> ReadAsync(CancellationToken cancellationToken);
}
