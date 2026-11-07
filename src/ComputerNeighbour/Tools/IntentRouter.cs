using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Tools;

/// <summary>Ground common explicit requests before asking a small model to narrate tool results.</summary>
public static class IntentRouter
{
    public static ToolRequest? Resolve(string text,PermissionOptions permissions)
    {
        var request=text.Trim();
        for(var i=0;i<5;i++)request=Regex.Replace(request,@"^(?:please[,\s]+|can you[,\s]+|could you[,\s]+|можешь[,\s]+|пожалуйста[,\s]+)","",RegexOptions.IgnoreCase);
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
    private static ToolRequest New(string name,object args)=>new(name,JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(args)));
}
