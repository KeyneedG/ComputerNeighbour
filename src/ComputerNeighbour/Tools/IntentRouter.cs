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
        request=Regex.Replace(request,"^(?:"+(string.IsNullOrWhiteSpace(characterName)?"":Regex.Escape(characterName)+"|")+@"Evelyn|Evelin|Эвелин\p{L}*)\s*[,!:]\s*","",RegexOptions.IgnoreCase);
        for(var i=0;i<5;i++)request=Regex.Replace(request,@"^(?:please[,\s]+|can you[,\s]+|could you[,\s]+|можешь[,\s]+|пожалуйста[,\s]+)","",RegexOptions.IgnoreCase);
        if(Regex.IsMatch(request,@"^(?:don't|do not|не)\s+(?:read|open|search|browse|читай|чтени\p{L}*|открывай|ищи)\b",RegexOptions.IgnoreCase))return null;
        var literalCreate=Regex.Match(request,@"^(?:write|create|создай|запиши|напиши)(?:\s+(?:a\s+)?(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|:]+)\s*(?::|\|)\s*(?<content>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(literalCreate.Success)return New("create_file",new {path=literalCreate.Groups["path"].Value,content=literalCreate.Groups["content"].Value});
        var literalEdit=Regex.Match(request,@"^(?:edit|измени|отредактируй)(?:\s+(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|]+)\s*\|\s*(?<old>[^|]+)\|(?<replacement>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(literalEdit.Success)return New("edit_file",new {path=literalEdit.Groups["path"].Value,oldText=literalEdit.Groups["old"].Value.Trim(),newText=literalEdit.Groups["replacement"].Value.Trim()});
        var anyUrl=Regex.Match(request,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
        if(anyUrl.Success)return New("read_web",new {url=anyUrl.Value.TrimEnd('.',',',';',')',']')});
        var browserTarget=Regex.IsMatch(request,@"\b(?:(?:current|active|this|open)\s+(?:(?:browser|web)\s+)?(?:browser|page|tab)|(?:in|from)\s+(?:the\s+)?browser|(?:текущ\p{L}*|активн\p{L}*|эт\p{L}*|открыт\p{L}*)\s+(?:страниц\p{L}*|вкладк\p{L}*|браузер\p{L}*)|(?:в|из)\s+браузер\p{L}*)\b|^(?:read|прочитай)\s+(?:the\s+)?(?:browser|браузер)\s*[.!?]?$",RegexOptions.IgnoreCase);
        if(browserTarget&&Regex.IsMatch(request,@"\b(?:read|summari[sz]e|analy[sz]e|прочит\p{L}*|прочти|перескажи|изложи|оцен\p{L}*)\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(request,@"[\p{L}\p{N}_.-]+[/\\][\p{L}\p{N}_.-]+"))return New("read_browser",new{});
        var news=Regex.IsMatch(request,@"\bnews\b|новост|новость",RegexOptions.IgnoreCase);
        var search=Regex.IsMatch(request,@"\b(?:search|look up|find)\b|найди|поищи|поискать|искать в интернет|погугли",RegexOptions.IgnoreCase);
        var recent=Regex.IsMatch(request,@"\b(?:latest|recent|fresh|today|new)\b|последн|свеж|актуальн|сегодня|новую|новые",RegexOptions.IgnoreCase);
        if(news&&(search||recent))return New("search_web",new {query=SearchQuery(request,true),news=true});
        if(search&&!Regex.IsMatch(request,@"\b(?:file|folder|directory)\b|файл|папк|каталог",RegexOptions.IgnoreCase))return New("search_web",new {query=SearchQuery(request,false),news=false});
        if(Regex.IsMatch(request,@"^(?:read|summari[sz]e|прочитай|прочитать|прочти|перескажи|изучи)\b",RegexOptions.IgnoreCase))
        {
            var url=Regex.Match(request,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
            if(url.Success)return New("read_web",new {url=url.Value.TrimEnd('.',',',';',')',']')});
            var file=Regex.Match(request,@"(?<path>[\p{L}\p{N}_.-]+[/\\][^\s""<>?]+)");
            if(file.Success)return New("read_file",new {path=file.Groups["path"].Value.TrimEnd('.',',',';',')',']')});
            if(Regex.IsMatch(request,@"\b(?:(?:current|active|this|open)\s+(?:(?:browser|web)\s+)?(?:browser|page|tab)|(?:in|from)\s+(?:the\s+)?browser|(?:текущ\p{L}*|активн\p{L}*|эт\p{L}*|открыт\p{L}*)\s+(?:страниц\p{L}*|вкладк\p{L}*|браузер\p{L}*)|(?:в|из)\s+браузер\p{L}*)\b|^(?:read|прочитай)\s+(?:the\s+)?(?:browser|браузер)\s*[.!?]?$",RegexOptions.IgnoreCase))return New("read_browser",new{});
        }
        var create=Regex.Match(request,@"^(?:write|create|создай|запиши|напиши)(?:\s+(?:a\s+)?(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|:]+)\s*(?::|\|)\s*(?<content>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(create.Success)return New("create_file",new {path=create.Groups["path"].Value,content=create.Groups["content"].Value});
        var edit=Regex.Match(request,@"^(?:edit|измени|отредактируй)(?:\s+(?:file|файл))?\s+(?<path>[\p{L}\p{N}_.-]+[/\\][^\s|]+)\s*\|\s*(?<old>[^|]+)\|(?<replacement>[\s\S]*)$",RegexOptions.IgnoreCase);
        if(edit.Success)return New("edit_file",new {path=edit.Groups["path"].Value,oldText=edit.Groups["old"].Value.Trim(),newText=edit.Groups["replacement"].Value.Trim()});
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
        var start=Regex.Match(request,@"\b(?:search(?:\s+for)?|look up|find)\s+|(?:найди|поищи|погугли)\s+",RegexOptions.IgnoreCase);
        var query=start.Success?request[(start.Index+start.Length)..]:request;
        query=Regex.Replace(query,@"\b(?:in the internet|on the internet|on the web|в интернете|пожалуйста)\b","",RegexOptions.IgnoreCase).Trim(' ','.','?','!');
        return query[..Math.Min(query.Length,500)];
    }
    private static ToolRequest New(string name,object args)=>new(name,JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(args)));
}
