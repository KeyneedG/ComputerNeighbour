using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Platform;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Internet;

namespace ComputerNeighbour.Tools;

public sealed record ToolRequest(string Tool, Dictionary<string,JsonElement>? Args = null);
public sealed record ToolResult(string Tool, bool Success, string Text,IReadOnlyList<WebCitation>? Sources=null,IReadOnlyList<SearchHit>? SearchResults=null)
{
    internal WebArticle? Article { get; init; }
}
public sealed record ToolContext(PortablePaths Paths, AppConfig Config, WindowsDesktop Desktop, IBrowserReader? Browser = null,IWebResearch? Web=null,IOpenDocumentReader? OpenDocuments=null);
public interface ICompanionTool
{
    string Name { get; }
    string Description { get; }
    bool ChangesComputer { get; }
    Task<string> ExecuteAsync(Dictionary<string,JsonElement> args, ToolContext context, CancellationToken ct);
}

public sealed class ToolRegistry(ToolContext context)
{
    private readonly Dictionary<string,ICompanionTool> tools = new(StringComparer.OrdinalIgnoreCase);
    public void Register(ICompanionTool tool) => tools.Add(tool.Name, tool);
    public string Describe(Func<string,bool>? include=null) => string.Join("\n", tools.Values.Where(t=>include is null||include(t.Name)).Select(t => $"{t.Name}: {t.Description}"));
    internal bool ChangesComputer(ToolRequest request) => tools.TryGetValue(request.Tool,out var tool)&&tool.ChangesComputer;
    public bool RequiresApproval(ToolRequest request) => tools.TryGetValue(request.Tool, out var tool) && tool.ChangesComputer && (IsFileChange(request) ? !context.Config.Permissions.FullFileAccess && (context.Config.Permissions.ConfirmActions || context.Config.Permissions.ConfirmFileChanges) : context.Config.Permissions.ConfirmActions);
    public static bool IsFileChange(ToolRequest request)=>request.Tool.Equals("create_file",StringComparison.OrdinalIgnoreCase)||request.Tool.Equals("edit_file",StringComparison.OrdinalIgnoreCase);
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, bool approved, CancellationToken ct)
    {
        if (!tools.TryGetValue(request.Tool, out var tool)) return new(request.Tool, false, "Unknown tool.");
        if (RequiresApproval(request) && !approved) return new(request.Tool, false, "Action requires explicit console approval; it has not run.");
        try { return tool is IEvidenceTool evidence?await evidence.ExecuteWithEvidenceAsync(request.Args??new(),context,ct):new(request.Tool, true, await tool.ExecuteAsync(request.Args ?? new(), context, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(request.Tool, false, ex.Message); }
    }
    public static ToolRegistry BuiltIn(ToolContext context)
    {
        var result = new ToolRegistry(context);
        result.Register(new ResearchTool("read_browser"));
        result.Register(new ResearchTool("read_web"));
        result.Register(new ResearchTool("search_web"));
        result.Register(new DelegateTool("create_file", "args: {path: relative allowed text file,content: complete literal text}. Prepare a new file; existing files cannot be overwritten. A diff/preview is shown before approved changes.",true,async(a,c,ct)=>
        {
            if(!c.Config.Permissions.WriteFiles)throw new UnauthorizedAccessException("File writing is disabled.");
            var workspace=new FileWorkspace(c.Paths,c.Config.Permissions.WritableDirectories,c.Config.Permissions.MaxWritableFileBytes);
            return await workspace.ApplyAsync(await workspace.PrepareCreateAsync(Get(a,"path"),Get(a,"content"),ct),ct);
        }));
        result.Register(new DelegateTool("edit_file", "args: {path: relative allowed text file,oldText: exact unique text to replace,newText: replacement text}. Read the file first. Changes use a preview, backup and stale-file check.",true,async(a,c,ct)=>
        {
            if(!c.Config.Permissions.WriteFiles)throw new UnauthorizedAccessException("File writing is disabled.");
            var workspace=new FileWorkspace(c.Paths,c.Config.Permissions.WritableDirectories,c.Config.Permissions.MaxWritableFileBytes);
            return await workspace.ApplyAsync(await workspace.PrepareEditAsync(Get(a,"path"),Get(a,"oldText"),Get(a,"newText"),ct),ct);
        }));
        var fileReader=new TextFileReader(context.Paths,context.Config.Permissions,context.OpenDocuments??new WindowsDocumentReader(context.Config.Desktop));
        result.Register(new DelegateTool("read_file", "args: {path: exact filename or path under a configured readable directory}. A filename checks open Notepad documents before searching disk.",false,
            (a,c,ct)=>fileReader.RunAsync(Get(a,"path"),true,a.TryGetValue("openOnly",out var value)&&value.ValueKind==JsonValueKind.True,ct)));
        result.Register(new DelegateTool("find_file", "args: {path: exact filename or path}. Find a text file in open Notepad or configured readable folders.",false,
            (a,c,ct)=>fileReader.RunAsync(Get(a,"path"),false,false,ct)));
        result.Register(new DelegateTool("open_app", "args: {name: configured application alias}, e.g. notepad or calculator. No shell commands or arguments.", true, (a,c,ct) =>
        {
            var name = Get(a,"name");
            var exe = ResolveApplication(c.Paths,c.Config.Permissions,name);
            c.Desktop.OpenAllowedApp(exe); return Task.FromResult("Application launch requested: " + name);
        }));
        result.Register(new DelegateTool("click", "args: {x: physical screen pixel,y: physical screen pixel}. Click primary button. Requires desktopInput permission.", true, (a,c,ct) =>
        {
            RequireInput(c); c.Desktop.Click(a["x"].GetInt32(),a["y"].GetInt32()); return Task.FromResult("Click sent.");
        }));
        result.Register(new DelegateTool("type_text", "args: {text: literal Unicode text}. Type into active window. Requires desktopInput permission.", true, (a,c,ct) =>
        {
            RequireInput(c); var text = Get(a,"text"); if(text.Length>4000) throw new ArgumentException("Text exceeds 4000 characters."); c.Desktop.TypeText(text); return Task.FromResult("Text sent to active window.");
        }));
        result.Register(new DelegateTool("press_key", "args: {key: ENTER|TAB|ESC|BACKSPACE|UP|DOWN|LEFT|RIGHT|CTRL+L|CTRL+A|CTRL+C|CTRL+V}. Requires desktopInput permission.", true, (a,c,ct) =>
        {
            RequireInput(c); c.Desktop.PressKey(Get(a,"key")); return Task.FromResult("Key sent.");
        }));
        return result;
    }
    internal static string ResolveApplication(PortablePaths paths,PermissionOptions permissions,string name)
    {
        if(!permissions.OpenApplications)throw new UnauthorizedAccessException("Opening apps is disabled.");
        if(!permissions.AllowedApplications.TryGetValue(name,out var exe))throw new UnauthorizedAccessException("Unknown application alias.");
        return !Path.IsPathRooted(exe)&&(exe.Contains('/')||exe.Contains('\\'))?paths.Inside(exe):exe;
    }
    internal static string Get(Dictionary<string,JsonElement> args,string key) => args.TryGetValue(key,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Missing string argument: " + key);
    private static void RequireInput(ToolContext c) { if(!c.Config.Permissions.DesktopInput) throw new UnauthorizedAccessException("Desktop input is disabled in config."); }
    private sealed record DelegateTool(string Name,string Description,bool ChangesComputer,Func<Dictionary<string,JsonElement>,ToolContext,CancellationToken,Task<string>> Run) : ICompanionTool
    {
        public Task<string> ExecuteAsync(Dictionary<string,JsonElement> args,ToolContext context,CancellationToken ct) => Run(args,context,ct);
    }
}

public sealed record PublicDocument(string Url,string ContentType,string Text);
public static class PublicWeb
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip=ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        var b=ip.GetAddressBytes();
        if (b.Length==4) return !(b[0] is 0 or 10 or 127 || b[0]>=224 || b[0]==169&&b[1]==254 || b[0]==172&&b[1] is >=16 and <=31 || b[0]==192&&b[1] is 0 or 168 || b[0]==192&&b[1]==88&&b[2]==99 || b[0]==192&&b[1]==0&&b[2]==2 || b[0]==100&&b[1] is >=64 and <=127 || b[0]==198&&b[1] is 18 or 19 || b[0]==198&&b[1]==51&&b[2]==100 || b[0]==203&&b[1]==0&&b[2]==113);
        return !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !ip.IsIPv6SiteLocal && (b[0]&0xFE)!=0xFC && b[0] is >=0x20 and <=0x3F && !(b[0]==0x20&&b[1]==0x01&&b[2]==0x0D&&b[3]==0xB8) && !(b[0]==0x20&&b[1]==0x02);
    }
    public static async Task<string> ReadAsync(string url,CancellationToken ct)=>(await ReadDocumentAsync(url,ct)).Text;
    public static async Task<PublicDocument> ReadDocumentAsync(string url,CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect=false, UseProxy=false, ConnectTimeout=TimeSpan.FromSeconds(10), AutomaticDecompression=DecompressionMethods.All };
        handler.ConnectCallback=async (context,token) =>
        {
            var ips=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
            if(ips.Length==0 || ips.Any(ip=>!IsPublic(ip))) throw new HttpRequestException("Private/local network URLs are not allowed.");
            Exception? last=null;
            foreach(var ip in ips)
            {
                var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(ip,context.DnsEndPoint.Port),token); return new NetworkStream(socket,ownsSocket:true); }
                catch(Exception ex) { socket.Dispose(); last=ex; }
            }
            throw new HttpRequestException("Unable to connect.",last);
        };
        using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComputerNeighbour/0.1 (article reader)");
        var uri=new Uri(url,UriKind.Absolute);
        for(int redirects=0;redirects<5;redirects++)
        {
            if(uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) throw new HttpRequestException("Only public HTTP/HTTPS URLs on standard ports are supported.");
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            if((int)response.StatusCode is >=300 and <=399 && response.Headers.Location is {} location) { uri=new Uri(uri,location); continue; }
            response.EnsureSuccessStatusCode();
            var type=response.Content.Headers.ContentType?.MediaType??"";
            if(!type.StartsWith("text/",StringComparison.OrdinalIgnoreCase) && type is not ("application/json" or "application/xml" or "application/rss+xml" or "application/atom+xml" or "application/xhtml+xml")) throw new HttpRequestException("URL does not return readable text, HTML or RSS.");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            using var output=new MemoryStream(); var buffer=new byte[8192]; int n;
            while((n=await stream.ReadAsync(buffer,ct))>0) { if(output.Length+n>2_000_000) throw new IOException("Article exceeds 2 MB."); output.Write(buffer,0,n); }
            return new(uri.AbsoluteUri,type,DecodeDocument(output.ToArray(),response.Content.Headers.ContentType?.CharSet));
        }
        throw new HttpRequestException("Too many redirects.");
    }

    internal static string DecodeDocument(byte[] bytes,string? charset)
    {
        static Encoding? Resolve(string? label)
        {
            if(string.IsNullOrWhiteSpace(label))return null;
            label=label.Trim().Trim('"','\'');
            try{return CodePagesEncodingProvider.Instance.GetEncoding(label)??Encoding.GetEncoding(label);}
            catch(ArgumentException){return null;}
        }
        var encoding=Resolve(charset);
        if(encoding is null)
        {
            var prefix=Encoding.Latin1.GetString(bytes,0,Math.Min(bytes.Length,4096));
            var meta=System.Text.RegularExpressions.Regex.Match(prefix,
                """<meta\b[^>]*?\bcharset\s*=\s*["']?([a-zA-Z0-9_.-]+)""",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            encoding=Resolve(meta.Groups[1].Value);
        }
        using var reader=new StreamReader(new MemoryStream(bytes,writable:false),encoding??Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
        return reader.ReadToEnd();
    }
}
