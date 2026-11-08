using System.Text.RegularExpressions;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Application;

internal enum WorkEffort { Quick, Standard, Deep }

internal sealed class WorkRequest
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Edit = @"(?:create|write|edit|update|fix|replace|save|append|создай|создать|напиши|запиши|сохрани|сохранить|измени|исправь|исправить|обнови|отредактируй|замени|добавь)";
    private const string Input = @"(?:click|type|press|scroll|enter|calculate|кликни|кликнуть|щ[её]лкни|нажми|нажать|введи|ввести|набери|набрать|напечатай|напечатать|прокрути|прокрутить|посчитай|посчитать)";
    private const string Focus = @"(?:focus|switch to|переключись|переключи)";
    private const string Open = @"(?:open|launch|открой|открыть|запусти|запустить|" + Focus + ")";
    private const string WriteText = @"(?:write|type|напиши|написать|запиши|записать|напечатай|напечатать|набери|набрать)";
    private const string NotepadDestination = @"\b(?:(?:in|into)\s+(?:the\s+)?notepad|в\s+блокнот[е]?)\b";
    private const string Read = @"(?:read|find|inspect|check|look at|search|прочитай|прочитать|прочти|найди|проверь|посмотри|поищи)";
    private const string FileWord = @"\b(?:files?|folders?|documents?|drafts?|notes?)\b|файл|папк|документ|черновик|заметк|[\\/]([\p{L}\p{N}_.-]+\.)";
    private const string WindowWord = @"\b(?:screen|desktop|window|dialog|preview|calculator|notepad|paint|mspaint)\b|экран|рабочий стол|окно|окн[аеоу]|диалог|калькулятор|блокнот|п[еэ]йнт|превью";
    private const string UiEdit = @"(?:choose|select|toggle|dismiss|close (?:the )?dialog|выбери|включи|выключи|закрой диалог)";
    private static readonly Regex WindowEdit = new(
        @"^(?:(?:in|в)\s+[^,\r\n]{1,45}\s+)?" + UiEdit + @"\b", IgnoreCase);
    private const string WebSearchWord = @"\b(?:search (?:the web|online)|look up|google|find online|on the (?:web|internet))\b|(?:в интернете|в сети|онлайн|погугли)";
    private const string Sequence = @"\b(?:and then|and|then|after that|затем|потом|после этого|и)\s+(?=(?:" +
        Edit + "|" + Input + "|" + Open + "|" + Read + @")\b)";
    private static readonly Regex Files = new(
        """["«“](?<path>[^"»”\r\n]+\.\p{L}[\p{L}\d]{0,11})["»”]|(?<![\p{L}\p{N}_./\\-])(?<path>(?:[a-z]:[/\\])?(?:[\p{L}\p{N}_.-]+[/\\])*[\p{L}\p{N}_.-]+\.\p{L}[\p{L}\d]{0,11})(?![\p{L}\p{N}_/\\-])""", IgnoreCase);
    private static readonly Regex OutputFolder = new(
        """\b(?:(?:in|into|to)\s+(?:the\s+)?(?:folder|directory)|в\s+(?:папку|каталог|директорию))\s+(?:["«“](?<path>[^"»”\r\n]+)["»”]|(?<path>(?:[a-z]:[/\\])?[\p{L}\p{N}_-]+(?:[/\\][\p{L}\p{N}_.-]+)*[/\\]?))""",
        IgnoreCase);
    private static readonly Regex OutputPathWithSeparator = new(
        @"\b(?:in|into|to|в)\s+(?<path>(?:[a-z]:[/\\])?(?:[\p{L}\p{N}_.-]+[/\\])+)(?![\p{L}\p{N}_.:/\\-])", IgnoreCase);
    private static readonly Regex WebUrl = new("""https?://[^\s<>"'«»]+""", IgnoreCase);
    private static readonly Regex QuotedText = new(""""[^"\r\n]*"|'[^'\r\n]*'|«[^»\r\n]*»|“[^”\r\n]*”"""", IgnoreCase);
    private static readonly Regex NotepadWrite = new(
        @"^(?:" + NotepadDestination + @"[,\s]+" + WriteText + @"\b|" + WriteText +
        @"\s+(?:(?!\b(?:about|how|о|об|про|как)\b)[^.!?\r\n])*?" + NotepadDestination + ")", IgnoreCase);
    private static readonly Regex NoLaunch = new(
        @"\b(?:(?:do not|don['’]t|never)\s+(?:open|launch)|without\s+(?:opening|launching)|не\s+(?:открывай|запускай)|без\s+(?:открытия|запуска))\b", IgnoreCase);
    private static readonly Regex Followup = new(
        @"^(?:(?:yes|okay|ok|now|then|да|ок|хорошо|теперь|тогда|давай)[,\s]+)*(?:do (?:it|that|the (?:first|second) option)|go ahead|сделай(?:\s+(?:это|так|(?:первый|второй) вариант))?|исправь|поправь|fix it|edit it|save it|сохрани|продолжай)[.!?\s]*$", IgnoreCase);
    private static readonly Regex NoChanges = new(
        @"\b(?:read only|read-only|no changes|without (?:making )?changes|only (?:read|inspect|look)|just (?:read|inspect|look)|do not (?:change|edit|write)|don['’]t (?:change|edit|write))\b|без (?:правок|изменений)|только (?:прочитай|посмотри|проверь)|(?:не|ничего не) (?:меняй|изменяй|редактируй|трогай|записывай)", IgnoreCase);
    private static readonly Regex NoInput = new(
        @"\b(?:do not|don['’]t|never)\s+(?:click|type|press|scroll|touch|interact)|не\s+(?:кликай|нажимай|вводи|печатай|прокручивай|управляй)|без\s+(?:кликов|ввода)", IgnoreCase);
    private static readonly Regex NoWeb = new(
        @"\b(?:offline|no (?:internet|web)|without (?:the )?(?:internet|web)|do not browse|don['’]t browse)\b|без\s+(?:интернета|сети)|не\s+(?:выходи в интернет|ищи в интернете|открывай ссылк\p{L}*)", IgnoreCase);

    internal WorkRequest(string goal, string previous, bool screenAllowed)
    {
        Goal = goal;
        var confirmsPrior = Followup.IsMatch(goal);
        var refersBack = confirmsPrior || Regex.IsMatch(goal, @"\b(?:it|that|this|there|here|это|его|е[её]|там|здесь)\b", IgnoreCase);
        PriorSubject = refersBack ? previous : "";
        Drawing = DrawingRequest.Parse(goal, previous) ?? (confirmsPrior ? DrawingRequest.Parse(PriorSubject) : null);
        var subject = Goal + "\n" + PriorSubject;
        var currentFiles = FileNames(goal);
        NamedFiles = currentFiles.Length > 0 ? currentFiles : FileNames(PriorSubject);
        ReadFiles = NamedFiles.Length > 0 || Regex.IsMatch(WebUrl.Replace(subject, ""), FileWord, IgnoreCase);
        var execution = IsExecution(goal);
        var actions = execution && !NoChanges.IsMatch(goal) && (HasVerb(goal, Edit) || !NoChanges.IsMatch(PriorSubject));
        WriteInNotepad = IsNotepadWrite(goal);
        WriteFiles = actions && ReadFiles && (WantsFileChange(goal) ||
            confirmsPrior && (HasVerb(goal, Edit) || WantsFileChange(PriorSubject)) ||
            PriorSubject.Length > 0 && Regex.IsMatch(goal, @"\b" + Edit + @"\s+(?:it|that|это|его|е[её])\b", IgnoreCase));
        if (WriteFiles)
        {
            var pathText = WebUrl.Replace(goal, "");
            var folder = OutputFolder.Match(pathText);
            if (!folder.Success) folder = OutputPathWithSeparator.Match(pathText);
            if (folder.Success) DestinationFolder = folder.Groups["path"].Value.TrimEnd('/', '\\');
        }
        OpenApps = actions && (WriteInNotepad || HasVerb(goal, Open) || confirmsPrior && HasVerb(PriorSubject, Open)) &&
            (!WriteInNotepad || !NoLaunch.IsMatch(goal));
        var windowSubject = Regex.IsMatch(subject, WindowWord, IgnoreCase);
        DesktopInput = actions && !NoInput.IsMatch(subject) && (WriteInNotepad || HasVerb(goal, Input) || HasVerb(goal, Focus) ||
            confirmsPrior && (HasVerb(PriorSubject, Input) || HasVerb(PriorSubject, Focus)) || windowSubject && HasVerb(goal, UiEdit));
        ObserveWindow = screenAllowed && TurnPlanner.PermitsDesktopObservation(PriorSubject) && (DesktopInput || windowSubject);
        var canReadOnline = execution && !NoWeb.IsMatch(subject);
        ReadBrowser = canReadOnline && Regex.IsMatch(subject, @"\b(?:browser|tab)\b|браузер|вкладк", IgnoreCase);
        SearchClauses = canReadOnline ? new[] { goal, PriorSubject }.SelectMany(text => Regex.Split(text, Sequence, IgnoreCase))
            .Where(clause => Regex.IsMatch(clause, WebSearchWord, IgnoreCase)).ToArray() : [];
        Urls = canReadOnline ? WebUrl.Matches(subject).Select(m => m.Value.TrimEnd('.', ',', ')', ']', '!', '?')).ToArray() : [];
    }

    internal string Goal { get; }
    internal string PriorSubject { get; }
    internal DrawingRequest? Drawing { get; }
    internal bool ReadFiles { get; }
    internal bool WriteFiles { get; }
    internal string? DestinationFolder { get; }
    internal bool WriteInNotepad { get; }
    internal bool OpenApps { get; }
    internal bool DesktopInput { get; }
    internal bool ObserveWindow { get; }
    internal bool ReadBrowser { get; }
    internal bool SearchWeb => SearchClauses.Length > 0;
    private string[] SearchClauses { get; }
    internal string[] Urls { get; }
    internal string[] NamedFiles { get; }

    internal static bool IsExecution(string request) => Followup.IsMatch(request) || IsDesktopAction(request) || Regex.IsMatch(request,
        @"^(?:(?:please|пожалуйста|тогда|теперь|да|then|now|first|сначала|давай|i want you to|(?:я )?хочу,? чтобы ты)[,\s]+)*(?:" + Edit + "|" + Input + "|" + Open + "|" + Read +
        "|" + UiEdit + @"|do|run|perform|compare|поправь|выполни|сделай|сравни|подготовь|разберись|проведи)\b", IgnoreCase);

    internal static bool IsDesktopAction(string request) => IsNotepadWrite(request) || Regex.IsMatch(request,
        @"^(?:(?:please|пожалуйста|теперь|now)[,\s]+)*(?:(?:in|в)\s+[^,\r\n]{1,45}\s+)?(?:" + Input + "|" + Focus + @")\b", IgnoreCase) ||
        WindowEdit.IsMatch(request) && Regex.IsMatch(request, WindowWord, IgnoreCase);

    internal static bool IsCompound(string request) => IsExecution(request) && Regex.IsMatch(request, Sequence, IgnoreCase);

    internal static bool IsFileWrite(string request) =>
        Regex.IsMatch(request, "^" + Edit + @"\b", IgnoreCase) && !NoChanges.IsMatch(request) && WantsFileChange(request);

    private static bool IsNotepadWrite(string request) => NotepadWrite.IsMatch(QuotedText.Replace(request, " "));

    internal bool MatchesFileTarget(string path)
    {
        if (NamedFiles.Length == 0) return true;
        var normalized = path.Replace('\\', '/');
        return NamedFiles.Any(named =>
        {
            var expected = named.Replace('\\', '/');
            return normalized.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                !expected.Contains('/') && normalized.EndsWith("/" + expected, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal bool AllowsSearchQuery(string query) => query.Length is >= 2 and <= 300 &&
        !query.Any(char.IsControl) && SearchClauses.Any(clause => clause.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string[] FileNames(string text) => Files.Matches(WebUrl.Replace(text, ""))
        .Select(m => m.Groups["path"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool WantsFileChange(string text) => Regex.Split(text, Sequence, IgnoreCase).Any(clause =>
        HasVerb(clause, Edit) && (FileNames(clause).Length > 0 || Regex.IsMatch(clause, @"\b(?:files?|folders?|documents?)\b|файл|папк|документ", IgnoreCase)));

    private static bool HasVerb(string text, string pattern) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])", IgnoreCase);
}
