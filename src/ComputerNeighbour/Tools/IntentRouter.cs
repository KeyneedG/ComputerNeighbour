using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Tools;

/// <summary>Ground common explicit requests before asking a small model to narrate tool results.</summary>
public static class IntentRouter
{
    public static ToolRequest? Resolve(string text,PermissionOptions permissions,string? characterName=null)
    {
        var request=text.Trim();
        if(Regex.IsMatch(request,@"^[""'«“]|^(?:she said|he said|the phrase|explain the words|i might)\b",RegexOptions.IgnoreCase))return null;
        request=CommandText(request,characterName);
        if(Regex.IsMatch(request,@"^(?:don't|do not|не)\s+(?:read|open|search|browse|читай|чтени\p{L}*|открывай|ищи)\b",RegexOptions.IgnoreCase))return null;
        var literalCreate=Regex.Match(request,@"^(?:write|create|создай|запиши|напиши)(?:\s+(?:a\s+)?(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|:]+)\s*(?::|\|)\s*(?<content>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(literalCreate.Success)return New("create_file",new {path=literalCreate.Groups["path"].Value,content=literalCreate.Groups["content"].Value});
        var literalEdit=Regex.Match(request,@"^(?:edit|измени|отредактируй)(?:\s+(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|]+)\s*\|\s*(?<old>[^|]+)\|(?<replacement>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(literalEdit.Success)return New("edit_file",new {path=literalEdit.Groups["path"].Value,oldText=literalEdit.Groups["old"].Value.Trim(),newText=literalEdit.Groups["replacement"].Value.Trim()});
        var anyUrl=Regex.Match(request,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
        if(anyUrl.Success&&(anyUrl.Index==0||Regex.IsMatch(request,@"^(?:read|open|fetch|summari[sz]e|analy[sz]e|check|прочитай|прочитать|прочти|открой|открыть|посмотри|перескажи|изучи|расскажи|объясни)\b",RegexOptions.IgnoreCase)))
            return New("read_web",new {url=anyUrl.Value.TrimEnd('.',',',';',')',']')});
        var browserTarget=Regex.IsMatch(request,@"\b(?:(?:current|active|this|open)\s+(?:(?:browser|web)\s+)?(?:browser|page|tab)|(?:in|from)\s+(?:the\s+)?browser|(?:текущ\p{L}*|активн\p{L}*|эт\p{L}*|открыт\p{L}*)\s+(?:страниц\p{L}*|вкладк\p{L}*|браузер\p{L}*)|(?:в|из)\s+браузер\p{L}*)\b|^(?:read|прочитай)\s+(?:the\s+)?(?:browser|браузер)\s*[.!?]?$",RegexOptions.IgnoreCase);
        if(browserTarget&&Regex.IsMatch(request,@"\b(?:read|summari[sz]e|analy[sz]e|прочит\p{L}*|прочти|перескажи|изложи|оцен\p{L}*)\b|^(?:look at|check|посмотри|взгляни|проверь)\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(request,@"[\p{L}\p{N}_.-]+[/\\][\p{L}\p{N}_.-]+"))return New("read_browser",new{});
        var news=Regex.IsMatch(request,@"\bnews\b|новост|новость",RegexOptions.IgnoreCase);
        var search=Regex.IsMatch(request,@"^(?:search|look up|find|google|найди|найти|поищи|поискать|погугли|погуглить)\b",RegexOptions.IgnoreCase);
        var recent=Regex.IsMatch(request,@"\b(?:latest|recent|fresh|today|new)\b|последн|свеж|актуальн|сегодня|новую|новые",RegexOptions.IgnoreCase);
        var find=Regex.IsMatch(request,@"^(?:find|locate|найди|найти|поищи|поискать|где)\b",RegexOptions.IgnoreCase);
        var read=Regex.IsMatch(request,@"^(?:read|summari[sz]e|look at|show the contents of|прочитай|прочитать|прочти|почитай|посмотри|глянь|покажи содержимое|перескажи|изучи)\b",RegexOptions.IgnoreCase);
        if(read||find)
        {
            var file=Regex.Match(request,@"[""«“](?<path>[^""»”\r\n]+\.[\p{L}\d]{1,8})[""»”]|(?<![\p{L}\p{N}_./\\-])(?<path>(?:[A-Za-z]:)?(?:[\p{L}\p{N}_.-]+[/\\]|[/\\])*(?:[\p{L}\p{N}_.-]+)\.[\p{L}\d]{1,8})(?![\p{L}\p{N}_/\\-])");
            if(file.Success)
            {
                var path=file.Groups["path"].Value;
                var openOnly=read&&Regex.IsMatch(request,@"\bnotepad\b|блокнот",RegexOptions.IgnoreCase);
                return New(read?"read_file":"find_file",new {path,openOnly});
            }
        }
        if(news&&(search||recent&&(read||Regex.IsMatch(request,@"\b(?:please|пожалуйста|можешь)\s+(?:open|read|откр?ой|прочти)\b",RegexOptions.IgnoreCase))))
            return New("search_web",new {query=SearchQuery(request,true),news=true});
        if(search&&!Regex.IsMatch(request,@"\b(?:file|folder|directory)\b|файл|папк|каталог",RegexOptions.IgnoreCase))
        {
            var query=SearchQuery(request,false);
            if(query.Length>0&&!IsWebCorrection(request))return New("search_web",new {query,news=false});
        }
        var open=Regex.Match(request,@"^(?:open|launch|start|открой|открыть|запусти|запустить)\s+(?<name>.+?)[.!?]*$",RegexOptions.IgnoreCase);
        if(open.Success)
        {
            var name=open.Groups["name"].Value.Trim();
            foreach(var alias in permissions.AllowedApplications.Keys)
                if(name.Equals(alias,StringComparison.OrdinalIgnoreCase))return New("open_app",new {name=alias});
            if(name.Equals("калькулятор",StringComparison.OrdinalIgnoreCase)&&permissions.AllowedApplications.ContainsKey("calculator"))return New("open_app",new {name="calculator"});
            if(name.Equals("блокнот",StringComparison.OrdinalIgnoreCase)&&permissions.AllowedApplications.ContainsKey("notepad"))return New("open_app",new {name="notepad"});
        }
        return null;
    }
    internal static string CommandText(string text,string? characterName=null)
    {
        var request=Regex.Replace(text.Trim(),"^(?:"+(string.IsNullOrWhiteSpace(characterName)?"":Regex.Escape(characterName)+"|")+@"Evelyn|Evelin|Эвелин\p{L}*)\s*[,!:]\s*","",RegexOptions.IgnoreCase);
        return Regex.Replace(request,@"^(?:(?:please|can you|could you|would you|можешь|могла бы|пожалуйста)[,\s]+)+","",RegexOptions.IgnoreCase);
    }
    internal static bool IsWebCorrection(string text)=>WebFollowupPrefix(text).Success;
    internal static Match WebFollowupPrefix(string text)=>Regex.Match(text,@"^(?:(?:no|actually|неа|нет|вообще-то)[,!\s]+)?(?:(?:check|look|search)\s+(?:on the (?:web|internet)|online)|(?:look it up|google it)(?:\s+(?:on the (?:web|internet)|online))?|(?:посмотри|проверь|поищи|найди|поискать|искать)\s+(?:это\s+)?(?:в интернете|в сети|в гугле|онлайн)|погугли это)\b",RegexOptions.IgnoreCase);
    public static bool IsSourceFollowup(string text)=>Regex.IsMatch(text,@"^\s*(?:sources?|citation|reference|источник\p{L}*)\s*[?!]?$|\b(?:give|show|send|provide|attach|where)\b.{0,50}\b(?:source|sources|link|url|citation)\b|(?:дай|покажи|прилож|можешь|где|какой).{0,60}(?:источник|исчтоник|ссылк)",RegexOptions.IgnoreCase);
    private static string SearchQuery(string request,bool news)
    {
        if(news)
        {
            var topic=Regex.Match(request,@"\b(?:about|news on)\s+(?<topic>[^.!?]+)|новост\p{L}*\s+(?:о|про|об)\s+(?<topic>[^.!?]+)",RegexOptions.IgnoreCase);
            if(topic.Success){var value=topic.Groups["topic"].Value.Trim();return "latest news "+value[..Math.Min(value.Length,450)];}
        }
        if(news&&Regex.IsMatch(request,@"technolog|\btech\b|техник|технолог",RegexOptions.IgnoreCase))return "latest technology news";
        if(news&&Regex.IsMatch(request,@"прочти\s+новую|read\s+(?:a\s+)?new\s+|latest\s+news\s*[.!?]?$",RegexOptions.IgnoreCase))return "latest technology news";
        var start=Regex.Match(request,@"^(?:search(?:\s+for)?|look up|find|google|найди|найти|поищи|поискать|погугли|погуглить)(?:\s+|$)",RegexOptions.IgnoreCase);
        var query=start.Success?request[start.Length..]:request;
        query=Regex.Replace(query,@"\b(?:in the internet|on the internet|on the web|в интернете|пожалуйста)\b","",RegexOptions.IgnoreCase).Trim(' ','.','?','!');
        return query[..Math.Min(query.Length,500)];
    }
    internal static ToolRequest New(string name,object args)=>new(name,JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(args)));
}
