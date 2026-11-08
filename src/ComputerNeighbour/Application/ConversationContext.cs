using System.Text.RegularExpressions;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;

namespace ComputerNeighbour.Application;

public static class ConversationContext
{
    public static List<ChatMessage> Build(string system,IReadOnlyList<MemoryEntry> history,byte[]? image,string currentInput,bool spokenReply=false)
    {
        var messages=new List<ChatMessage> {new("system",system)};
        var dialogue=history.Where(e=>e.Role is "user" or "assistant").ToList();
        var lastUser=dialogue.FindLastIndex(e=>e.Role=="user");
        for(var index=0;index<dialogue.Count;index++)
        {
            var entry=dialogue[index];
            if(entry.Role=="assistant")
            {
                // Keep the last occurrence in the model context; preserve the full ledger on disk.
                if(dialogue.Skip(index+1).Any(e=>e.Role=="assistant"&&ReplyQuality.IsRepeated(entry.Text,e.Text)))continue;
                if(ReplyQuality.IsEmptyOffer(entry.Text))continue;
                if(ReplyQuality.ClaimsComputerAction(entry.Text))continue;
                messages.Add(new("assistant",entry.Text,IsHistory:true));
            }
            else
            {
                var text=index==lastUser?currentInput:entry.Text;
                if(!entry.SpeakerId.Equals("user",StringComparison.OrdinalIgnoreCase))text="[Speaker "+entry.SpeakerId+"] "+text;
                // Make the language instruction local to the latest request as well as the system prompt.
                if(index==lastUser)text+="\n[Reply in English. Answer the request itself; do not offer to answer later.]";
                if(index==lastUser&&spokenReply)text+="\n[The app speaks your reply aloud. Write the spoken words only. Copy any script the user explicitly asks to hear verbatim, without an introduction, stage directions, or follow-up.]";
                messages.Add(new("user",text,index==lastUser?image:null,IsHistory:index!=lastUser));
            }
        }
        return messages;
    }
    public static bool NeedsTools(string input)
    {
        if(Regex.IsMatch(input,@"https?://|[\p{L}\p{N}_.-]+[/\\][\p{L}\p{N}_.-]+|\bapplication\b.*\brunning\b",RegexOptions.IgnoreCase))return true;
        var command=input.Trim();
        for(var i=0;i<4;i++)command=Regex.Replace(command,@"^(?:please[,\s]+|can you[,\s]+|could you[,\s]+|would you[,\s]+|пожалуйста[,\s]+|можешь[,\s]+)","",RegexOptions.IgnoreCase);
        if(Regex.IsMatch(command,@"^(?:(?:open|launch|start)\s+(?!(?:up|with|about|to)\b)|(?:click|press|открой|запусти|нажми|введи|напечатай)\s+|type\s+(?!of\b))",RegexOptions.IgnoreCase))return true;
        return Regex.IsMatch(command,@"^(?:keep|make|save|write|edit|create|update|read|сохрани|запиши|создай|измени|отредактируй|прочитай|сделай)\b.*\b(?:file|note|document|article|browser|tab|page|файл\p{L}*|заметк\p{L}*|документ\p{L}*|стать\p{L}*|браузер\p{L}*|страниц\p{L}*|вкладк\p{L}*)\b",RegexOptions.IgnoreCase);
    }
}

public static class ReplyQuality
{
    public static string FirstScreenThought(string reply)=>string.Join(" ",Regex.Split(reply.Trim(),@"(?<=[.!?])\s+").Take(2));

    public static bool IsShortScreenReaction(string reply,IEnumerable<string> recentReplies)
    {
        var text=reply.Trim();
        if(!IsBriefAside(text,recentReplies))return false;
        if(Regex.IsMatch(text,@"^(?:[-*#]|\d+[.)]\s|(?:visible details|image analysis|observations|scene description|the (?:screen|screenshot|image|picture|photo) (?:shows|displays|depicts)|i (?:can see|see (?:an?|the|your)\b)|it looks like you(?: are|['’]re))\b)",RegexOptions.IgnoreCase))
            return false;
        var normalized=Normalize(text);
        if(Regex.IsMatch(normalized,@"^(?:that s|that is|that looks|this is|this looks|it looks|looks) (?:a |very |really )?(?:nice|interesting|cool|pretty|beautiful|great|neat)(?: (?:photo|picture|image|shot))?$"))return false;
        return !DeniesToolAccess(text,true,false,false);
    }

    internal static bool IsBriefAside(string reply,IEnumerable<string> recentReplies)
    {
        var text=reply.Trim();
        return text.Length is >0 and <=260&&!text.Contains('\n')&&!text.Contains('\r')
            &&Regex.Matches(text,@"[.!?]+(?=\s|$)").Count<=2&&Normalize(text).Length>0
            &&!IsEmptyOffer(text)&&!RepeatsAny(text,recentReplies);
    }

    internal static bool RepeatsAny(string reply,IEnumerable<string> recentReplies)
    {
        var normalized=Normalize(reply);
        return recentReplies.Any(previous=>Normalize(previous)==normalized||IsRepeated(previous,reply));
    }

    public static bool IsNameQuestion(string text)=>Regex.IsMatch(text,@"\bwhat(?:'s| is) my (?:preferred )?name\b|\bwhat (?:did I ask you to call me|I asked you to call me)\b|\bremind me (?:what|how) I asked you to call me\b|как\s+меня\s+зовут|как\s+я\s+просил\p{L}*.*называть",RegexOptions.IgnoreCase);
    public static bool IsNameAnswer(string answer,string name)
    {
        if(Regex.IsMatch(answer,@"\b(?:my name is|call me|i am evelyn|i'm evelyn|what is your name)\b",RegexOptions.IgnoreCase))return false;
        var romanized=new System.Text.StringBuilder();
        const string cyrillic="абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] latin=["a","b","v","g","d","e","yo","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","","y","","e","yu","ya"];
        foreach(var ch in name.ToLowerInvariant()){var index=cyrillic.IndexOf(ch);romanized.Append(index>=0?latin[index]:ch.ToString());}
        var normalized=Normalize(answer);
        return Regex.IsMatch(normalized,@"(?<!\p{L})"+Regex.Escape(Normalize(name))+@"(?!\p{L})",RegexOptions.IgnoreCase)||Regex.IsMatch(normalized,@"(?<!\p{L})"+Regex.Escape(romanized.ToString())+@"(?!\p{L})",RegexOptions.IgnoreCase);
    }
    public static bool HasIdentityConfusion(string answer,string assistant,string user)
    {
        if(user.Length==0||assistant.Equals(user,StringComparison.OrdinalIgnoreCase))return false;
        var self=Regex.Match(answer,@"\b(?:I am|I'm|my name is|call me)\s+(?<name>[\p{L}]+)",RegexOptions.IgnoreCase);
        if(self.Success&&IsNameAnswer("Your name is "+self.Groups["name"].Value,user))return true;
        return Regex.IsMatch(answer,@"\b(?:your name is|you are|you're)\s+"+Regex.Escape(assistant)+@"\b",RegexOptions.IgnoreCase);
    }
    public static bool DeniesPersistentMemory(string answer)=>Regex.IsMatch(answer,@"\b(?:no|don't have|do not have|cannot retain|can't retain)\s+(?:any\s+)?(?:persistent\s+|long.term\s+)?memor(?:y|ies)\b.{0,70}\b(?:sessions|restarts|conversations)\b",RegexOptions.IgnoreCase);
    internal static bool ContainsVoiceDirection(string answer)=>Regex.IsMatch(answer,
        @"(?im)(?:^\s*i (?:can['’]t|cannot|am unable to) (?:read|say|speak)[^.!?\r\n]{0,70}\b(?:out loud|aloud)\b|(?<!\*)\*(?:sings|laughs|sighs|whispers|speaks|smiles|voice +\w+)\b[^*\r\n]{0,100}\*(?!\*))")
        ||Regex.IsMatch(answer,@"(?im)[.!?""”][ \t]+\((?:[a-z]+ +){0,2}(?:joy|sadness|surprise|anger|fear|warmth|concern)\)[ \t]*\r?$");
    public static bool ClaimsFreshScreenObservation(string answer)
    {
        const string display=@"(?:your|the) (?:screen|desktop)";
        return Regex.IsMatch(answer,@"\bI (?:can see|see|notice|noticed) (?:that )?"+display+@"\b",RegexOptions.IgnoreCase)
            ||Regex.IsMatch(answer,@"\bI(?:['’]m| am) (?:just )?(?:listening and )?watching "+display+@"\b",RegexOptions.IgnoreCase)
            ||Regex.IsMatch(answer,@"(?:^|[.!?]\s+)(?:right now[,\s]+)?"+display+@"\s+(?:shows|displays|is showing)\b",RegexOptions.IgnoreCase)
            ||Regex.IsMatch(answer,@"\bI (?:can see|see)\b[^.!?\r\n]{0,100}\bon "+display+@"\b|\bI (?:just |now )?(?:captured|checked|took a screenshot of|looked at) "+display+@" (?:just now|for this|now)\b|\bI just (?:captured|checked|looked at) "+display+@"\b",RegexOptions.IgnoreCase)
            ||Regex.IsMatch(answer,@"(?:^|[.!?]\s+)(?:(?:that|this|it) (?:looks|appears)|(?:the|your) (?:preview|render|image|photo|lighting|shadows?|skin tones?) (?:looks?|appears?|is|are))\s+(?:(?:a bit|a little|much|slightly|noticeably)\s+)?(?:lighter|darker|brighter|dimmer|sharper|blurrier|softer|clearer)\b[^.!?\r\n]{0,100}\b(?:now|this time)\b",RegexOptions.IgnoreCase);
    }

    internal static bool ClaimsComputerAction(string answer)
    {
        const string firstPerson = @"\bI(?:['’]ll| will|['’]m(?: going to)?| am(?: going to)?)\s+(?:(?:now|just|currently|go ahead and|start)\s+)*";
        const string computerTarget = @"(?:file|folder|document|app(?:lication)?|window|browser|tab|notepad|paint|calculator|canvas|screen|desktop|cursor|clipboard)";
        const string appTarget = @"\s+(?:up\s+)?(?:(?:the|your|a|an|new)\s+)*(?:" + computerTarget + @"|(?-i:[A-Z][\p{L}\p{N}.-]+))\b";
        const RegexOptions ignoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        return Regex.IsMatch(answer, firstPerson +
            @"(?:(?:open(?:ing)?|launch(?:ing)?|switch(?:ing)? to)" + appTarget +
            @"|(?:sav(?:e|ing)|writ(?:e|ing)|typ(?:e|ing)|edit(?:ing)?|creat(?:e|ing)|draw(?:ing)?|sketch(?:ing)?|click(?:ing)?|mov(?:e|ing)|press(?:ing)?)\b[^.!?\r\n]{0,100}\b" + computerTarget + @"\b)",
            ignoreCase) ||
            Regex.IsMatch(answer, @"\bI(?:['’]ve| have)?\s+(?:(?:just|already|successfully)\s+)*(?:opened|launched|switched to)" + appTarget, ignoreCase) ||
            Regex.IsMatch(answer, @"\bI(?:['’]ve| have| did)?\s+(?:(?:just|already|successfully)\s+)*(?:(?:start(?:ed)?\s+(?:typing|writing|drawing|editing))|typed|wrote|drew|saved|edited)\b[^.!?\r\n]{0,80}\b" + computerTarget + @"\b", ignoreCase) ||
            Regex.IsMatch(answer, @"\bI(?:['’]ve got| have)\s+(?:the|my|a)\s+(?:drawing\s+)?brush ready\b|" +
                @"\bI(?:['’]m| am)\s+(?:drafting|drawing|sketching)\b[^.!?\r\n]{0,70}\b(?:right before (?:our|your) eyes|in front of you)\b", ignoreCase);
    }

    public static bool DeniesToolAccess(string answer,bool screen,bool files,bool web)
    {
        const string denial=@"\bI (?:cannot|can['’]t|could not|couldn['’]t|do not|don['’]t|did not|didn['’]t|am unable to|am not allowed to|only)\s+";
        return screen&&(Regex.IsMatch(answer,denial+@"(?:actually |directly |really )?(?:see|view|browse|capture|access|look at|have access to|receive|take)\s+(?:any |a |the |your |this )?(?:screen\b|desktop\b|screenshot\b|image\b)",RegexOptions.IgnoreCase)
                ||Regex.IsMatch(answer,denial+@"(?:see|tell|know|determine)\s+what\s+(?:you are|you['’]re)\s+(?:currently\s+)?doing\b.{0,60}\b(?:unless|until|without)\b.{0,80}\bscreenshot\b",RegexOptions.IgnoreCase))
            ||files&&Regex.IsMatch(answer,denial+@"(?:(?:read|open|access|have access to) (?:any |your |local |the )?(?:files|file system)|(?:read|process|see) (?:what|text) you paste)",RegexOptions.IgnoreCase)
            ||web&&Regex.IsMatch(answer,denial+@"(?:actually |directly )?(?:browse|search|access|have access to|use)\s+(?:the )?(?:live )?(?:web|internet|online)",RegexOptions.IgnoreCase);
    }
    public static string Normalize(string text)=>Regex.Replace(text.ToLowerInvariant(),@"[^\p{L}\p{N}]+"," ").Trim();
    public static bool IsRepeated(string first,string second)
    {
        var a=Normalize(first);var b=Normalize(second);
        if(a==b)return a.Length>=35;
        if(Math.Min(a.Length,b.Length)<35)return false;
        var aw=a.Split(' ').ToHashSet();var bw=b.Split(' ').ToHashSet();
        return (double)aw.Intersect(bw).Count()/Math.Max(aw.Count,bw.Count)>0.88;
    }
    public static bool IsEmptyOffer(string text)
    {
        var normalized=Normalize(text);
        return normalized.Length<300&&Regex.IsMatch(normalized,@"\bi can (?:tell|share|explain|talk)\b.*\b(?:if you|would you|interested|like to)\b")
            ||normalized is "certainly" or "of course" or "sure";
    }
}
