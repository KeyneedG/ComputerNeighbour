namespace ComputerNeighbour.Browser;

public sealed class BrowserOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxCharacters { get; set; } = 16000;
    public int ReadTimeoutSeconds { get; set; } = 8;
    public bool PublicUrlFallback { get; set; } = true;
    public List<string> AllowedProcesses { get; set; } = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];
}

public sealed record BrowserPage(string Browser, string Title, string? Url, string Text, bool Truncated,string ReadMethod="accessibility");

public static class BrowserAddress
{
    public static string? Normalize(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        value=value.Trim();
        if(Uri.TryCreate(value,UriKind.Absolute,out var absolute)&&absolute.Scheme is "http" or "https" or "file")return absolute.AbsoluteUri;
        if(value.Length>4096||value.Any(char.IsWhiteSpace)||value.Contains(':')||!value.Contains('.'))return null;
        if(Uri.TryCreate("https://"+value,UriKind.Absolute,out var inferred)&&inferred.HostNameType==UriHostNameType.Dns&&inferred.Host.Contains('.')&&string.IsNullOrEmpty(inferred.UserInfo))return inferred.AbsoluteUri;
        return null;
    }
}

public interface IBrowserReader
{
    Task<BrowserPage> ReadAsync(CancellationToken cancellationToken);
}
