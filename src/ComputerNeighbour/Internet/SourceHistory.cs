using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Internet;

public sealed class SourceHistory(PortablePaths paths)
{
    private IReadOnlyList<WebCitation> sources=Array.Empty<WebCitation>();
    private bool loaded;
    public IReadOnlyList<WebCitation> Sources=>sources;
    public async Task InitializeAsync(CancellationToken ct)
    {
        if(loaded)return;
        var file=paths.Inside("data/memory/web-sources.json");
        if(File.Exists(file)&&new FileInfo(file).Length<200000)
            try{sources=(JsonSerializer.Deserialize<List<WebCitation>>(await File.ReadAllTextAsync(file,ct),AppConfig.Json)??[]).Where(s=>s is not null&&WebParsing.IsWebUrl(s.Url)).Take(20).ToArray();}catch(JsonException){sources=[];}
        loaded=true;
    }
    public async Task RecordAsync(IReadOnlyList<WebCitation> actual,CancellationToken ct)
    {
        await InitializeAsync(ct);
        var next=actual.Where(s=>WebParsing.IsWebUrl(s.Url)).DistinctBy(s=>s.Url).Take(20).ToArray();
        if(next.Length==0)return;
        var file=paths.Inside("data/memory/web-sources.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp=paths.Inside("data/memory/web-sources.json.tmp");
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(next,AppConfig.Json),ct);File.Move(temp,file,true);sources=next;
    }
    public async Task ClearAsync(CancellationToken ct)
    {
        var file=paths.Inside("data/memory/web-sources.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp=paths.Inside("data/memory/web-sources.json.tmp");await File.WriteAllTextAsync(temp,"[]",ct);File.Move(temp,file,true);sources=[];loaded=true;
    }
    public string Format()=>sources.Count==0?"I don't have a verified source saved for that earlier answer. I won't invent a URL. Ask me to search again or provide the article link.":"Verified sources from the last lookup:\n"+string.Join("\n",sources.Select((s,i)=>$"[{i+1}] {s.Title}\n{s.Url}\n{(s.PublishedAt is {} date?$"Published: {date:yyyy-MM-dd HH:mm zzz}. ":"")}Retrieved: {s.RetrievedAt:yyyy-MM-dd HH:mm zzz}. {(s.Kind=="search-result"?"Search result only; full article not read.":"")}"));
    public static string AddVerifiedLinks(string answer,IReadOnlyList<WebCitation> actual)
    {
        var allowed=actual.Select(s=>s.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        answer=Regex.Replace(answer,@"https?://[^\s<>""\]\)]+",m=>allowed.Contains(m.Value.TrimEnd('.',',',';','/'))?m.Value:"[unverified link removed]",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
        return answer.Trim()+"\n\nSources:\n"+string.Join("\n",actual.DistinctBy(s=>s.Url).Select((s,i)=>$"[{i+1}] {s.Title}\n{s.Url}\n"+(s.PublishedAt is {} date?$"Published: {date:yyyy-MM-dd HH:mm zzz}":"Publication date: unavailable")+(s.Kind=="search-result"?"; search snippet only":"")));
    }
    public static string ForSpeech(string reply)
    {
        var index=reply.IndexOf("\n\nSources:\n",StringComparison.Ordinal);
        if(index>=0)return reply[..index];
        return reply.StartsWith("Verified sources from",StringComparison.Ordinal)?"I've printed the verified source links in the console.":reply;
    }
}
