using System.Globalization;
using System.Text.RegularExpressions;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

internal sealed record WorkTaskCommand(string Action, string Target = "", string Value = "", bool Implicit = false)
{
    internal const string Usage = """
        /tasks                            List saved tasks
        /task save NAME | GOAL | PROGRESS | NEXT STEP
        /task resume NAME                 Pick up a saved task
        /task attach NAME | FILE OR URL    Link a material to a task
        /task find NAME | TEXT             Find text in the task's linked files
        /task compare NAME                 Compare its two most recently linked text files
        /task remind NAME | tomorrow 09:00 Set or replace its reminder
        /task cancel NAME                  Cancel its reminder
        """;

    internal static WorkTaskCommand? Parse(string input, string characterName)
    {
        var text = IntentRouter.CommandText(input, characterName).Trim();
        if (text.Equals("/tasks", StringComparison.OrdinalIgnoreCase)) return new("list");
        if (text.Equals("/task", StringComparison.OrdinalIgnoreCase)) return new("help");
        if (text.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[6..].Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            var action = parts[0].ToLowerInvariant();
            var tail = parts.Length > 1 ? parts[1] : "";
            if (action == "save") return new("save", Value: tail);
            var fields = tail.Split('|', 2, StringSplitOptions.TrimEntries);
            return new(action, Name(fields[0]), fields.Length > 1 ? fields[1] : "");
        }
        text = text.TrimEnd('?', '!', '.').TrimEnd();

        if (Matches(text, @"^(?:мои задачи|покажи (?:мои |сохран[её]нные )?задачи|(?:show |list )?(?:my|saved) tasks)$").Success)
            return new("list");
        if (Matches(text, @"^(?:запомни|сохрани|запиши|remember|save)[,\s]+(?:(?:где|на ч[её]м) (?:я|мы) (?:остановил\p{L}*|закончил\p{L}*)|where (?:i|we) (?:left off|stopped)|(?:the )?(?:задачу|состояние задачи|task|checkpoint))\b").Success)
            return new("capture", Value: input);

        var match = Matches(text, @"^(?:где (?:мы|я) остановил\p{L}*|на ч[её]м (?:мы|я) остановил\p{L}*|where did (?:i|we) leave off|что (?:у меня )?осталось|what(?:'s| is) left)(?:\s+(?:с|по|над|with|on|for)\s+(?:(?:задаче|задачей|(?:the )?task)\s+)?(?<task>.+))?$");
        if (match.Success) return new("resume", Name(match.Groups["task"].Value), Implicit: !match.Groups["task"].Success);
        match = Matches(text, @"^(?:продолжим|продолжи|continue|resume)(?:\s+(?:с(?: задачей)?|задачу|with(?: the task)?|(?:the )?task)\s+(?<task>.+))?$");
        if (match.Success) return new("resume", Name(match.Groups["task"].Value), Implicit: !match.Groups["task"].Success);

        match = Matches(text, @"^(?:привяжи|прикрепи|attach)\s+(?<value>.+?)\s+(?:к задаче|to (?:the )?task)\s+(?<task>.+)$");
        if (match.Success) return new("attach", Name(match.Groups["task"].Value), Name(match.Groups["value"].Value));
        match = Matches(text, @"^(?:найди|поищи)\s+в\s+(?:материалах|файлах)\s+задачи\s+(?<task>.+?)\s*:\s*(?<value>.+)$|^(?:найди|поищи)\s+(?<value>.+?)\s+в (?:материалах|файлах) задачи\s+(?<task>.+)$|^(?:find|search for)\s+(?<value>.+?)\s+in (?:the )?files (?:for|of) (?:the )?task\s+(?<task>.+)$");
        if (match.Success) return new("find", Name(match.Groups["task"].Value), Name(match.Groups["value"].Value));
        match = Matches(text, @"^(?:найди|поищи)\s+в\s+(?:материалах|файлах)\s+задачи\s+(?<task>.+)$");
        if (match.Success) return new("find", Name(match.Groups["task"].Value));
        match = Matches(text, @"^(?:сравни (?:версии|черновики)(?: задачи)?|compare (?:the )?(?:versions|drafts)(?: (?:of |for )?(?:the )?task)?|что (?:изменилось|поменялось) (?:в|по) задаче|what changed in (?:the )?task)(?:\s+(?<task>.+))?$");
        if (match.Success) return new("compare", Name(match.Groups["task"].Value));
        if (Matches(text, @"^(?:что (?:изменилось|поменялось)|what changed)$").Success)
            return new("compare", Implicit: true);

        match = Matches(text, @"^(?:отмени напоминание|cancel (?:the )?reminder)(?:\s+(?:по задаче|для задачи|for (?:the )?task)\s+(?<task>.+))?$");
        if (match.Success) return new("cancel", Name(match.Groups["task"].Value));
        match = Matches(text, @"^(?:напомни(?: мне)?|remind me)\s+(?<value>(?:через|завтра|in|tomorrow)\b.+|завтра|tomorrow)$");
        if (match.Success && TrySchedule(match.Groups["value"].Value, DateTimeOffset.Now, out _, out var target))
        {
            // Keep ordinary reminders about other subjects out of the task route.
            if (target.Length == 0 || Matches(target, @"^(?:продолжить|вернуться к|to (?:continue|resume))\b").Success)
            {
                var name = Regex.Replace(target, @"^(?:продолжить(?:\s+(?:задачу|с))?|вернуться к(?:\s+задаче)?|to (?:continue|resume)(?:\s+(?:with|the task|task))?)\s*", "", RegexOptions.IgnoreCase);
                return new("remind", Name(name), match.Groups["value"].Value);
            }
        }
        return null;
    }

    internal static bool TrySchedule(string text, DateTimeOffset now, out DateTimeOffset at, out string remainder)
    {
        at = default;
        remainder = "";
        text = Regex.Replace(text, @"^(?:через\s+полчаса|in\s+half an hour)\b", "in 30 minutes", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(?:через\s+час|in\s+an hour)\b", "in 1 hour", RegexOptions.IgnoreCase);
        var match = Matches(text, @"^(?:in|через)\s+(?<n>[0-9]{1,5})\s*(?<unit>minutes?|mins?|минут\p{L}*|мин|hours?|hrs?|час\p{L}*|days?|день|дня|дней)(?:\s+(?<rest>.+))?$");
        if (match.Success)
        {
            var number = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var minutes = number * (unit.StartsWith('h') || unit.StartsWith("час", StringComparison.Ordinal) ? 60 :
                unit.StartsWith('d') || unit.StartsWith('д') ? 1440 : 1);
            if (minutes is < 1 or > 525600) return false;
            at = now.AddMinutes(minutes);
            remainder = match.Groups["rest"].Value;
            return true;
        }
        match = Matches(text, @"^(?:tomorrow|завтра)(?:\s+(?:(?:at|в)\s+)?(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2}))?)?(?:\s+(?<rest>.+))?$");
        if (!match.Success) return false;
        var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) : 9;
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour > 23 || minute > 59) return false;
        var local = now.Date.AddDays(1).AddHours(hour).AddMinutes(minute);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) return false;
        at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        remainder = match.Groups["rest"].Value;
        return true;
    }

    internal static string Name(string text) => text.Trim().Trim('"', '\'', '«', '»', '“', '”').Trim();

    private static Match Matches(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
