using System.Text.RegularExpressions;

namespace ComputerNeighbour.Application;

internal sealed record DrawingRequest(string Subject, bool OpenInPaint, string? Limitation)
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Paint = @"(?:(?:microsoft|ms)\s+)?(?:paint|mspaint|п[еэ]йнт[е]?|паинт[е]?)";
    private static readonly Regex Direct = new(
        @"^(?:(?<paint>(?:in|в)\s+" + Paint + @")[,\s]+)?(?:draw|sketch|try (?:drawing|sketching)|нарисуй|нарисовать|изобрази|набросай|сделай рисунок|создай иллюстрацию)\s+(?<subject>.+)$",
        IgnoreCase);
    private static readonly Regex SelfImageQuestion = new(
        @"[?.!]+\s*(?:как\s+(?:ты\s+)?себя\s+представляешь|how do you (?:see|picture|imagine) yourself)[?.!\s]*$", IgnoreCase);
    private static readonly Regex SelfPortrait = new(
        @"^(?:себя|yourself|(?:a\s+)?self[- ]portrait|(?:свой\s+)?автопортрет)$", IgnoreCase);
    private static readonly Regex PaintDestination = new(@"\b(?:in|в)\s+" + Paint + @"\b", IgnoreCase);
    private static readonly Regex UnsupportedCanvas = new(
        @"\b(?:current|existing|open)\s+(?:canvas|drawing|image)\b|\b(?:without saving|do not save|don['’]t save|with (?:the |your )?mouse)\b|(?:текущ|открыт|существующ)\p{L}*\s+(?:холст|рисунк|изображени)\p{L}*|\b(?:не\s+сохраняй|без\s+сохранения|мышкой|мышью)\b",
        IgnoreCase);
    private static readonly Regex OtherEditor = new(
        @"\b(?:in|в)\s+(?:photoshop|фотошоп\p{L}*|krita|крит[ае]|gimp|illustrator|figma|inkscape|procreate)\b", IgnoreCase);
    private static readonly Regex NoLaunch = new(
        @"\b(?:(?:do not|don['’]t)\s+open|without opening|не\s+(?:открывай|запускай)|без запуска)\b", IgnoreCase);

    internal bool IsSelfPortrait => SelfPortrait.IsMatch(Subject);

    internal static DrawingRequest? Parse(string request, string previous = "")
    {
        var drawing = ParseDirect(request);
        if (drawing is not { OpenInPaint: false, Limitation: null } || previous.Length == 0 || NoLaunch.IsMatch(request))
            return drawing;
        if (ParseDirect(previous) is { OpenInPaint: true, Limitation: null } prior &&
            (drawing.IsSelfPortrait && prior.IsSelfPortrait || string.Equals(drawing.Subject, prior.Subject, StringComparison.OrdinalIgnoreCase)))
            return drawing with { OpenInPaint = true };
        return drawing;
    }

    private static DrawingRequest? ParseDirect(string request)
    {
        request = SelfImageQuestion.Replace(request, "").TrimEnd();
        var command = Direct.Match(request);
        if (!command.Success) return null;
        var subject = command.Groups["subject"].Value;
        if (Regex.IsMatch(subject, @"^(?:a\s+)?(?:conclusion|parallel|comparison|distinction|breath|attention)\b|\b(?:ascii|in text|текстом)\b", IgnoreCase))
            return null;
        return FromBrief(subject, request, command.Groups["paint"].Success);
    }

    internal static DrawingRequest FromBrief(string subject, string authority, bool startsWithPaint = false)
    {
        var open = (startsWithPaint || PaintDestination.IsMatch(authority)) && !NoLaunch.IsMatch(authority);
        string? limitation = UnsupportedCanvas.IsMatch(authority)
            ? "My drawing workflow renders a new PNG. It needs permission to save a file and cannot draw directly on the existing canvas."
            : OtherEditor.IsMatch(authority)
                ? "I can render a PNG and open it in Paint. This drawing workflow does not control the requested editor."
                : null;
        subject = PaintDestination.Replace(subject, "").Trim(' ', ',', '.', '?', '!');
        return new(subject, open, limitation);
    }
}
