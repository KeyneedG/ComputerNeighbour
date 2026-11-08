using System.Text.RegularExpressions;
using ComputerNeighbour.Memory;
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
    private const string WebSearchWord = @"\b(?:search (?:the web|online)|look up|google|find online|on the (?:web|internet))\b|(?:в интернете|в сети|онлайн|(?:за|по)гугли)";
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
        @"\b(?:offline|no (?:internet|web)|without (?:the )?(?:internet|web)|(?:do not|don['’]t|never) (?:browse|search|google|look (?:it up|up)))\b|без\s+(?:интернета|сети)|не\s+(?:выходи в интернет|ищи|поискать|гугли|погугли|загугли\p{L}*|открывай ссылк\p{L}*)\b", IgnoreCase);

    internal static string WithoutQuotedText(string text) => QuotedText.Replace(text, match => new string(' ', match.Length));
    internal static bool PermitsWebLookup(string text) => !NoWeb.IsMatch(WithoutQuotedText(text));

    internal WorkRequest(string goal, string previous, bool screenAllowed, SemanticWork? semantic = null, WorkArtifact? revision = null)
    {
        Goal = goal;
        Revision = revision;
        var confirmsPrior = Followup.IsMatch(goal) || semantic?.ContinuesPrevious == true;
        var authority = semantic?.ContinuesPrevious == true ? previous : goal;
        RequestsSynthesis = Regex.IsMatch(authority,@"\b(?:summari[sz]e|summary|draft|synthesis|overview)\b|\b(?:сводк\p{L}*|перескаж\p{L}*|резюме|обзор|сочини)\b",IgnoreCase);
        RequestsCatalog = Regex.IsMatch(authority,@"\b(?:catalog(?:ue)?|bibliograph\w*|list of (?:works|publications))\b|\b(?:каталог\p{L}*|библиограф\p{L}*|список произведений)\b",IgnoreCase);
        CopySourceText = semantic?.Includes("copy_text") == true;
        var refersBack = confirmsPrior || Regex.IsMatch(goal, @"\b(?:it|that|this|there|here|это|его|е[её]|там|здесь)\b", IgnoreCase);
        PriorSubject = refersBack ? previous : "";
        Drawing = DrawingRequest.Parse(goal, previous) ?? (confirmsPrior ? DrawingRequest.Parse(PriorSubject) : null);
        if (Drawing is null && semantic?.Includes("draw") == true && semantic.DrawingSubject.Length > 0)
            Drawing = DrawingRequest.FromBrief(semantic.DrawingSubject, authority);
        var subject = Goal + "\n" + PriorSubject;
        var currentFiles = FileNames(goal);
        NamedFiles = currentFiles.Length > 0 ? currentFiles : FileNames(PriorSubject);
        var fileSubject = NamedFiles.Length > 0 || Regex.IsMatch(WebUrl.Replace(subject, ""), FileWord, IgnoreCase);
        var semanticFiles = semantic?.Includes("write_file") == true &&
            (semantic.Includes("desktop_input") == false || fileSubject);
        ReadFiles = semantic?.Includes("read_file") == true || semanticFiles || fileSubject;
        var execution = semantic is not null || IsExecution(goal);
        var actions = execution && !NoChanges.IsMatch(authority) && (HasVerb(goal, Edit) || !NoChanges.IsMatch(PriorSubject));
        WriteInNotepad = IsNotepadWrite(authority) || semantic?.Includes("desktop_input") == true &&
            Regex.IsMatch(authority, @"\b(?:notepad|блокнот\p{L}*|text editor|текстов\p{L}* редактор\p{L}*)\b", IgnoreCase);
        WriteFiles = actions && ReadFiles && (semanticFiles || WantsFileChange(goal) ||
            confirmsPrior && (HasVerb(goal, Edit) || WantsFileChange(PriorSubject)) ||
            PriorSubject.Length > 0 && Regex.IsMatch(goal, @"\b" + Edit + @"\s+(?:it|that|это|его|е[её])\b", IgnoreCase));
        if (WriteFiles)
        {
            var pathText = WebUrl.Replace(authority, "");
            var folder = OutputFolder.Match(pathText);
            if (!folder.Success) folder = OutputPathWithSeparator.Match(pathText);
            if (folder.Success) DestinationFolder = folder.Groups["path"].Value.TrimEnd('/', '\\');
        }
        OpenApps = actions && !NoLaunch.IsMatch(authority) &&
            (semantic?.Includes("open_app") == true || WriteInNotepad || HasVerb(goal, Open) || confirmsPrior && HasVerb(PriorSubject, Open));
        var windowSubject = Regex.IsMatch(subject, WindowWord, IgnoreCase);
        DesktopInput = actions && !NoInput.IsMatch(subject) && (semantic?.Includes("desktop_input") == true || WriteInNotepad || HasVerb(goal, Input) || HasVerb(goal, Focus) ||
            confirmsPrior && (HasVerb(PriorSubject, Input) || HasVerb(PriorSubject, Focus)) || windowSubject && HasVerb(goal, UiEdit));
        ObserveWindow = screenAllowed && TurnPlanner.PermitsDesktopObservation(PriorSubject) && (DesktopInput || windowSubject);
        var canReadOnline = execution && !NoWeb.IsMatch(subject);
        ReadBrowser = canReadOnline && (semantic?.Includes("read_browser") == true || Regex.IsMatch(subject, @"\b(?:browser|tab)\b|браузер|вкладк", IgnoreCase));
        SearchClauses = !canReadOnline ? [] :
            semantic?.Includes("search_web") == true && Regex.IsMatch(QuotedText.Replace(authority, ""), WebSearchWord, IgnoreCase)
                ? [authority]
                : new[] { goal, PriorSubject }.SelectMany(text => Regex.Split(text, Sequence, IgnoreCase))
                    .Where(clause => Regex.IsMatch(clause, WebSearchWord, IgnoreCase)).ToArray();
        Urls = canReadOnline ? WebUrl.Matches(subject).Select(m => m.Value.TrimEnd('.', ',', ')', ']', '!', '?')).ToArray() : [];
        if (revision is not null && !NoChanges.IsMatch(goal))
        {
            if (revision.Kind == WorkArtifactKind.Image)
                Drawing = DrawingRequest.FromBrief(revision.Goal,goal,revision.OpenInPaint);
            else if (revision.Kind == WorkArtifactKind.TextFile)
            {
                ReadFiles = WriteFiles = true;
                NamedFiles = [revision.Path];
                DestinationFolder = Path.IsPathFullyQualified(revision.Path) ? Path.GetDirectoryName(revision.Path) : null;
            }
            else if (!NoLaunch.IsMatch(goal))
            {
                WriteInNotepad = OpenApps = true;
            }
        }
    }

    internal string Goal { get; }
    internal WorkArtifact? Revision { get; }
    internal string PriorSubject { get; }
    internal DrawingRequest? Drawing { get; }
    internal bool ReadFiles { get; }
    internal bool WriteFiles { get; }
    internal bool CopySourceText { get; }
    internal bool RequestsSynthesis { get; }
    internal bool RequestsCatalog { get; }
    internal string? DestinationFolder { get; }
    internal bool WriteInNotepad { get; }
    internal bool OpenApps { get; }
    internal bool DesktopInput { get; }
    internal bool ObserveWindow { get; }
    internal bool ReadBrowser { get; }
    internal bool SearchWeb => SearchClauses.Length > 0;
    internal string SearchAuthority => string.Join("\n", SearchClauses);
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

    internal static bool HasMutationClause(string request) =>
        Regex.Split(WithoutQuotedText(request), @"[,;.!?]|\b(?:and|then|и|затем|потом)\b", IgnoreCase)
            .Any(clause => Regex.IsMatch(IntentRouter.CommandText(clause), "^(?:"+Edit+"|"+Input+"|"+Open+"|"+UiEdit+@")\b", IgnoreCase));

    internal static bool IsShortConsent(string request) => Followup.IsMatch(request.Trim()) ||
        Regex.IsMatch(request.Trim(), @"^(?:давай|да|хорошо|yes|okay|ok)[.!?]*$", IgnoreCase);

    internal static bool IsAdvice(string request) => Regex.IsMatch(request,
        @"^(?:how (?:do|can|should|could) I\b|how to\b|what (?:is|does)\b|why\b|explain\b|как (?:мне|самостоятельно)\b|как\s+(?:открыть|запустить|написать|сделать)\b|почему\b|что такое\b|объясни\b)", IgnoreCase);

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

    internal static bool CompatibleArtifactTarget(string request,string path) => FileNames(request).All(named =>
        named.Replace('\\','/').Equals(path.Replace('\\','/'),StringComparison.OrdinalIgnoreCase) ||
        !named.Contains('/') && !named.Contains('\\') && named.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase));

    internal bool AllowsSearchQuery(string query) => query.Length is >= 2 and <= 300 &&
        !query.Any(char.IsControl) && SearchClauses.Any(clause => clause.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string[] FileNames(string text) => Files.Matches(WebUrl.Replace(text, ""))
        .Select(m => m.Groups["path"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool WantsFileChange(string text) => Regex.Split(text, Sequence, IgnoreCase).Any(clause =>
        HasVerb(clause, Edit) && (FileNames(clause).Length > 0 || Regex.IsMatch(clause, @"\b(?:files?|folders?|documents?)\b|файл|папк|документ", IgnoreCase)));

    private static bool HasVerb(string text, string pattern) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])", IgnoreCase);
}
