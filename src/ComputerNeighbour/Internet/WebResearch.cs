using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Internet;

public sealed class InternetOptions
{
    public string SearchProvider { get; set; }="auto";
    public bool LookupUnfamiliarTerms { get; set; }=true;
    public int MaxResults { get; set; }=5;
    public int MaxArticles { get; set; }=2;
    public int MaxArticleCharacters { get; set; }=16000;
    public int RequestTimeoutSeconds { get; set; }=20;
    public int NewsMaxAgeDays { get; set; }=7;
    public List<string> TechnologyNewsFeeds { get; set; }=["https://feeds.bbci.co.uk/news/technology/rss.xml","https://feeds.arstechnica.com/arstechnica/index","https://techcrunch.com/feed/"];
}
public sealed record WebCitation(string Title,string Url,DateTimeOffset RetrievedAt,string Kind="article",DateTimeOffset? PublishedAt=null);
public sealed record SearchHit(string Title,string Url,string Snippet,DateTimeOffset? PublishedAt=null);
public sealed record SearchResponse(string Query,string Provider,IReadOnlyList<SearchHit> Results,IReadOnlyList<string> Warnings);
public sealed record WebArticle(string Title,string Url,string Text,bool Truncated,DateTimeOffset? PublishedAt=null);
public interface IWebResearch
{
    Task<SearchResponse> SearchAsync(string query,bool news,CancellationToken ct);
    Task<WebArticle> ReadAsync(string url,CancellationToken ct);
}

public sealed class HttpWebResearch(InternetOptions options,Func<string,CancellationToken,Task<PublicDocument>>? fetch=null):IWebResearch
{
    private readonly Func<string,CancellationToken,Task<PublicDocument>> fetch=fetch??PublicWeb.ReadDocumentAsync;
    private async Task<PublicDocument> FetchAsync(string url,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try{return await fetch(url,timeout.Token);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new TimeoutException("The public website did not respond in time.");}
    }
    public async Task<WebArticle> ReadAsync(string url,CancellationToken ct)
    {
        var document=await FetchAsync(url,ct);
        var article=WebParsing.Article(document,options.MaxArticleCharacters);
        if(string.IsNullOrWhiteSpace(article.Text))throw new IOException("The page did not contain readable text.");
        if(Regex.IsMatch(article.Title,@"^(?:just a moment|access denied|robot check|attention required)",RegexOptions.IgnoreCase))
            throw new IOException("The website returned an access check, not the article. Open it in the browser and use /browser.");
        return article;
    }
    public async Task<SearchResponse> SearchAsync(string query,bool news,CancellationToken ct)
    {
        query=query.Trim();
        if(query.Length is <2 or >600)throw new ArgumentException("Search query must contain 2–600 characters.");
        var warnings=new List<string>();
        if(news&&Regex.IsMatch(query,@"^\s*(?:(?:latest|recent|today)\s+)?(?:technology|tech)\s+news\s*$",RegexOptions.IgnoreCase))
        {
            var batches=await Task.WhenAll(options.TechnologyNewsFeeds.Take(8).Select(async url=>
            {
                try{return (Hits:WebParsing.Rss((await FetchAsync(url,ct)).Text),Error:(string?)null);}
                catch(Exception ex) when(ex is not OperationCanceledException){return (Hits:(IReadOnlyList<SearchHit>)Array.Empty<SearchHit>(),Error:"News feed unavailable: "+new Uri(url).Host+" ("+ex.Message+")");}
            }));
            warnings.AddRange(batches.Where(b=>b.Error is not null).Select(b=>b.Error!));
            var cutoff=DateTimeOffset.UtcNow.AddDays(-options.NewsMaxAgeDays);
            var recent=batches.SelectMany(b=>b.Hits).Where(h=>h.PublishedAt is {} date&&date>=cutoff&&date<=DateTimeOffset.UtcNow.AddHours(2)).DistinctBy(h=>h.Url).OrderByDescending(h=>h.PublishedAt).Take(options.MaxResults).ToArray();
            if(recent.Length>0)return new(query,"publisher RSS feeds",recent,warnings);
            warnings.Add("Publisher feeds did not return recent dated articles. Search results below may have unverified dates.");
        }
        var providers=options.SearchProvider=="auto"?new[]{"duckduckgo","bing-rss"}:new[]{options.SearchProvider};
        foreach(var provider in providers)
        {
            try
            {
                var address=provider=="bing-rss"?$"https://www.bing.com/{(news?"news/":"")}search?format=rss&q={Uri.EscapeDataString(query)}":$"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}{(news?"&df=w":"")}";
                var response=await FetchAsync(address,ct);
                var hits=(provider=="bing-rss"?WebParsing.Rss(response.Text):WebParsing.DuckDuckGo(response.Text)).Where(h=>!news||h.PublishedAt is null||h.PublishedAt>=DateTimeOffset.UtcNow.AddDays(-options.NewsMaxAgeDays)&&h.PublishedAt<=DateTimeOffset.UtcNow.AddHours(2)).DistinctBy(h=>h.Url).Take(options.MaxResults).ToArray();
                if(hits.Length>0)return new(query,provider,hits,warnings);
                warnings.Add(provider+" returned no readable search results (possibly a captcha).");
            }
            catch(Exception ex) when(ex is not OperationCanceledException){warnings.Add(provider+": "+ex.Message);}
        }
        throw new IOException("Web search did not return usable results. "+string.Join(" ",warnings));
    }
}

public static class WebParsing
{
    private static string Replace(string value,string pattern,string replacement)=>Regex.Replace(value,pattern,replacement,RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2));
    public static string Text(string html)
    {
        var text=Replace(html,@"<(script|style|noscript|svg|nav|footer|header)\b[^>]*>[\s\S]*?</\1\s*>"," ");
        text=Replace(text,@"</(?:p|div|article|section|h[1-6]|li|tr)>|<br\s*/?>","\n");
        text=Replace(text,@"<[^>]+>"," ");text=WebUtility.HtmlDecode(text);
        text=Replace(text,@"[ \t\f\v]+"," ");text=Replace(text,@" *\r?\n *","\n");
        return Replace(text,@"\n{3,}","\n\n").Trim();
    }
    public static WebArticle Article(PublicDocument document,int maximum)
    {
        var html=document.Text;
        var titleMatch=Regex.Match(html,@"<title\b[^>]*>([\s\S]*?)</title>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2));
        var title=titleMatch.Success?Text(titleMatch.Groups[1].Value):new Uri(document.Url).Host;
        var body=Regex.Match(html,@"<(?<tag>article|main)\b[^>]*>(?<body>[\s\S]*?)</\k<tag>\s*>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2));
        var text=document.ContentType.Contains("html",StringComparison.OrdinalIgnoreCase)?Text(body.Success?body.Groups["body"].Value:html):html.Trim();
        DateTimeOffset? published=null;
        foreach(Match tag in Regex.Matches(html,@"<meta\b[^>]*>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2)))
        {
            var property=Attribute(tag.Value,"property")??Attribute(tag.Value,"name");
            if(property is not null&&new[]{"article:published_time","datePublished","pubdate","date"}.Contains(property,StringComparer.OrdinalIgnoreCase)&&DateTimeOffset.TryParse(Attribute(tag.Value,"content"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)){published=date;break;}
        }
        return new(title,document.Url,text[..Math.Min(maximum,text.Length)],text.Length>maximum,published);
    }
    private static string? Attribute(string tag,string name)
    {
        var match=Regex.Match(tag,"\\b"+Regex.Escape(name)+"\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
        return match.Success?WebUtility.HtmlDecode(match.Groups["v"].Value):null;
    }
    public static IReadOnlyList<SearchHit> Rss(string xml)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2_000_000});
        var doc=XDocument.Load(reader);var results=new List<SearchHit>();
        foreach(var item in doc.Descendants().Where(e=>e.Name.LocalName is "item" or "entry").Take(100))
        {
            string Value(string name)=>item.Elements().FirstOrDefault(e=>e.Name.LocalName==name)?.Value??"";
            var link=Value("link");if(string.IsNullOrWhiteSpace(link))link=item.Elements().FirstOrDefault(e=>e.Name.LocalName=="link")?.Attribute("href")?.Value??"";
            link=CleanLink(link);
            if(!IsWebUrl(link))continue;
            DateTimeOffset? date=DateTimeOffset.TryParse(Value("pubDate") is {Length:>0} d?d:Value("published"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed)?parsed:null;
            var description=Value("description");if(description.Length==0)description=Value("summary");
            results.Add(new(Text(Value("title")),link,Text(description)[..Math.Min(Text(description).Length,1000)],date));
        }
        return results;
    }
    public static IReadOnlyList<SearchHit> DuckDuckGo(string html)
    {
        var results=new List<SearchHit>();
        foreach(Match match in Regex.Matches(html,@"<a\b(?<attrs>[^>]*\bclass\s*=\s*[""'][^""']*\bresult__a\b[^""']*[""'][^>]*)>(?<title>[\s\S]*?)</a>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2)))
        {
            var url=CleanLink(Attribute(match.Groups["attrs"].Value,"href")??"");
            if(!IsWebUrl(url))continue;
            var next=html.Substring(match.Index+match.Length,Math.Min(3000,html.Length-match.Index-match.Length));
            var snippet=Regex.Match(next,@"<(?:a|div)\b[^>]*\bclass=[""'][^""']*result__snippet[^""']*[""'][^>]*>(?<s>[\s\S]*?)</(?:a|div)>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
            results.Add(new(Text(match.Groups["title"].Value),url,snippet.Success?Text(snippet.Groups["s"].Value):""));
        }
        return results;
    }
    public static bool IsWebUrl(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&string.IsNullOrEmpty(uri.UserInfo);
    private static string CleanLink(string value)
    {
        value=WebUtility.HtmlDecode(value.Trim());if(value.StartsWith("//"))value="https:"+value;
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri))return "";
        if(uri.Host.Equals("duckduckgo.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".duckduckgo.com",StringComparison.OrdinalIgnoreCase)||uri.Host.Equals("bing.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".bing.com",StringComparison.OrdinalIgnoreCase))
            foreach(var pair in uri.Query.TrimStart('?').Split('&')){var parts=pair.Split('=',2);if(parts.Length==2&&parts[0] is "uddg" or "url")return Uri.UnescapeDataString(parts[1].Replace('+',' '));}
        return value;
    }
}
