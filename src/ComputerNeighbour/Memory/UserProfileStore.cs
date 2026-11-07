using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Memory;

/// <summary>Explicit communication instructions from the main user, never inferred from assistant speech.</summary>
public sealed record CommunicationPreferences
{
    public bool PreferDirectSpeech { get; init; }
    public bool ReducedInitiative { get; init; }
    public bool AvoidFiller { get; init; }
    [JsonIgnore]
    public string Description => string.Join(" ", new[]
    {
        PreferDirectSpeech ? "Speak directly and answer the actual question." : "",
        ReducedInitiative ? "The user explicitly asked for less initiative. Do not comment on every action; prefer quiet unless there is a concrete reason to speak." : "",
        AvoidFiller ? "Avoid filler, repeated introductions, offers to answer instead of answering, and unnecessary follow-up questions." : ""
    }.Where(value => value.Length > 0));
}

/// <summary>
/// Small grounded profile kept independently of generated memory notes. Only direct main-user statements
/// can update it; a model-generated summary or another speaker cannot rename the main user.
/// </summary>
public sealed class UserProfileStore
{
    private const string RelativeFile = "data/memory/user-profile.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex Quoted = new("\"[^\"\\r\\n]*\"|«[^»\\r\\n]*»|“[^”\\r\\n]*”|‘[^’\\r\\n]*’|(?<![\\p{L}])'[^'\\r\\n]*'(?![\\p{L}])", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Sentences = new(@"[^.!?;\r\n]+[.!?;]?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Filler = new(@"^(?:(?:хорошо|ладно|окей|okay|ok|well|пожалуйста|please|я\s+думаю|i\s+think|i\s+would\s+like\s+you\s+to|i'd\s+like\s+you\s+to|i\s+want\s+you\s+to|тебе\s+стоит|тебе\s+нужно|тебе\s+следует|you\s+should|and|и)\b[,\s:]+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> NonNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "you", "we", "please", "thanks", "thank", "but", "and", "or", "from", "now", "if", "when", "what", "how", "why", "instead", "not", "yes", "no",
        "я", "ты", "мы", "вы", "он", "она", "пожалуйста", "но", "и", "или", "если", "теперь", "нет", "да", "не", "как", "что", "меня"
    };
    private readonly PortablePaths paths;
    private readonly string fallbackName;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ProfileState state;
    private bool initialized;

    public UserProfileStore(string root, string fallbackName)
    {
        paths = new PortablePaths(root);
        this.fallbackName = MeaningfulFallback(fallbackName);
        state = Fresh();
    }

    /// <summary>Empty means not known. The internal speaker label "user" is never presented as a person's name.</summary>
    public string PreferredName => Volatile.Read(ref state).PreferredName;
    public CommunicationPreferences Preferences => Volatile.Read(ref state).Preferences;
    public string? RecoveryNotice { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            var file = paths.Inside(RelativeFile);
            var backup = paths.Inside(RelativeFile + ".bak");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file))
            {
                try { state = await ReadAsync(file, cancellationToken); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    if (!File.Exists(backup)) throw;
                    state = await ReadAsync(backup, cancellationToken);
                    File.Copy(file, paths.Inside(RelativeFile + ".corrupt"), true);
                    await SaveAsync(state, keepBackup: false, cancellationToken);
                    RecoveryNotice = "Recovered the previous user profile; the damaged profile was retained as user-profile.json.corrupt.";
                }
            }
            else if (File.Exists(backup))
            {
                state = await ReadAsync(backup, cancellationToken);
                await SaveAsync(state, keepBackup: false, cancellationToken);
                RecoveryNotice = "Recovered the user profile from its backup.";
            }
            else await SaveAsync(state, keepBackup: false, cancellationToken);
            if (state.PreferredName.Length == 0 && fallbackName.Length > 0)
            {
                state = state with { PreferredName = fallbackName, NameSource = "Configured main-user name.", UpdatedAt = DateTimeOffset.UtcNow };
                await SaveAsync(state, keepBackup: true, cancellationToken);
            }
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task ObserveAsync(string text, string speakerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            if (!IsMainSpeaker(speakerId) || text.Length > 20000) return;
            var previous = state;
            var next = previous;
            // Quoted dialogue belongs to its quoted speaker, even when pasted by the user.
            var directText = Quoted.Replace(text, " ");
            foreach (Match part in Sentences.Matches(directText))
            {
                var sentence = part.Value.Trim();
                if (sentence.Length == 0) continue;
                var instruction = StripFiller(sentence);
                var name = ExtractName(instruction);
                if (name is not null)
                    next = next with { PreferredName = name, NameSource = sentence[..Math.Min(sentence.Length, 500)] };
                var preferences = ReadPreferences(instruction, next.Preferences);
                if (preferences != next.Preferences)
                    next = next with { Preferences = preferences, PreferenceSource = sentence[..Math.Min(sentence.Length, 500)] };
            }
            if (next == previous) return;
            next = next with { UpdatedAt = DateTimeOffset.UtcNow };
            await SaveAsync(next, keepBackup: true, cancellationToken);
            Volatile.Write(ref state, next);
        }
        finally { gate.Release(); }
    }

    public string DescribeContext()
    {
        var current = Volatile.Read(ref state);
        var output = new StringBuilder("GROUNDED MAIN-USER PROFILE (explicit user statements; not model-generated guesses):\n");
        if (string.IsNullOrEmpty(current.PreferredName))
            output.AppendLine("The user's preferred name is not known. 'user' and 'User' are internal labels, not the person's name. Do not address them as User or invent a name.");
        else
            output.AppendLine("The main user's preferred name is " + current.PreferredName + ". Use this name consistently; do not ask for it again unless the user requests a change.");
        if (current.Preferences.Description.Length > 0) output.AppendLine("Explicit communication preferences: " + current.Preferences.Description);
        return output.ToString();
    }

    /// <summary>Reset explicitly learned facts; an actual configured name remains the fallback.</summary>
    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var reset = Fresh();
            await SaveAsync(reset, keepBackup: false, cancellationToken);
            // Replace recovery copies with the reset rather than leaving forgotten facts available on recovery.
            foreach (var relative in new[] { RelativeFile + ".bak", RelativeFile + ".corrupt" })
                if (File.Exists(paths.Inside(relative))) await File.WriteAllTextAsync(paths.Inside(relative), JsonSerializer.Serialize(reset, Json), cancellationToken);
            Volatile.Write(ref state, reset);
            RecoveryNotice = null;
        }
        finally { gate.Release(); }
    }

    private bool IsMainSpeaker(string? speakerId) => !string.IsNullOrWhiteSpace(speakerId) &&
        (speakerId.Equals("user", StringComparison.OrdinalIgnoreCase) || fallbackName.Length > 0 && speakerId.Equals(fallbackName, StringComparison.OrdinalIgnoreCase) || state.PreferredName.Length > 0 && speakerId.Equals(state.PreferredName, StringComparison.OrdinalIgnoreCase));

    private static string StripFiller(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            var stripped = Filler.Replace(text, "").TrimStart();
            if (stripped == text) break;
            text = stripped;
        }
        return text;
    }

    private static string? ExtractName(string instruction)
    {
        if (instruction.EndsWith('?')) return null;
        // The command portion is case-insensitive while additional name words must look like names.
        var prefix = Regex.Match(instruction, @"^(?:(?:меня\s+)?называй\s+меня|меня\s+зовут|call\s+me|my\s+name\s+is)\s+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (!prefix.Success) return null;
        var rest = instruction[prefix.Length..];
        var match = Regex.Match(rest, @"^(?<name>[\p{L}][\p{L}\p{M}'’\-]{0,39}(?:[ \t]+[\p{Lu}][\p{L}\p{M}'’\-]{0,39}){0,2})(?=$|[\s,.!?;:])", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        var parts = match.Groups["name"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).TakeWhile(p => !NonNameWords.Contains(p)).ToArray();
        if (parts.Length == 0) return null;
        var name = string.Join(' ', parts);
        return name.Length <= 80 && MeaningfulFallback(name).Length > 0 ? name : null;
    }

    private static CommunicationPreferences ReadPreferences(string text, CommunicationPreferences current)
    {
        static bool Is(string input, string pattern) => Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        var next = current;
        if (Is(text, @"^(?:(?:тебе\s+)?не\s+(?:обязательно|нужно|надо|стоит)\s+комментировать\s+(?:каждое|все)|(?:поубавь|поубавить|уменьши|уменьшить|снизь|снизить|убавь|поумерь)\b.{0,70}(?:инициатив|активност)|(?:be|become)\s+less\s+(?:proactive|initiating)|reduce\s+(?:your\s+)?(?:initiative|proactivity)|don['’]t\s+comment\s+on\s+every|only\s+speak\s+when\s+you\s+have\s+something\s+to\s+say)"))
            next = next with { ReducedInitiative = true };
        if (Is(text, @"^(?:будь\s+(?:более\s+)?инициатив|можешь\s+быть\s+(?:более\s+)?инициатив|(?:be|become)\s+more\s+proactive|(?:increase|restore)\s+(?:your\s+)?(?:initiative|proactivity))"))
            next = next with { ReducedInitiative = false };
        if (Is(text, @"^(?:будь\s+(?:более\s+)?(?:прям|откровенн|конкретн|кратк)|говори\s+(?:прямо|по\s+существу|конкретно|коротко)|be\s+(?:more\s+)?(?:direct|straightforward|concise)|speak\s+plainly|answer\s+directly)"))
            next = next with { PreferDirectSpeech = true };
        if (Is(text, @"^(?:не\s+(?:(?:стоит|нужно|надо)\s+)?(?:из\s+себя\s+)?(?:выдавливай|выдавливать|выжимай|выжимать)\b.{0,40}(?:слов|речь)|не\s+говори\s+(?:лишь\s+бы|ради)|говори\s+по\s+существу|(?:avoid|skip)\s+(?:the\s+)?(?:filler|small\s+talk)|don['’]t\s+(?:force|pad|fill|say\s+things\s+just)|only\s+speak\s+when\s+you\s+have\s+something\s+to\s+say)"))
            next = next with { AvoidFiller = true };
        return next;
    }

    private static string MeaningfulFallback(string? value)
    {
        var name = value?.Trim() ?? "";
        return name.Length is 0 or >80 || name.Any(char.IsControl) || new[] { "user", "unknown", "main user", "пользователь", "неизвестно" }.Contains(name, StringComparer.OrdinalIgnoreCase) ? "" : name;
    }

    private ProfileState Fresh() => new() { PreferredName = fallbackName, NameSource = fallbackName.Length > 0 ? "Configured main-user name." : "" };
    private void EnsureInitialized() { if (!initialized) throw new InvalidOperationException("Initialize the user profile before observing statements."); }
    private static async Task<ProfileState> ReadAsync(string file, CancellationToken cancellationToken)
    {
        if (new FileInfo(file).Length > 64000) throw new InvalidDataException("User profile exceeds its size limit.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Object || new[] { "version", "preferredName", "preferences" }.Any(key => !document.RootElement.TryGetProperty(key, out _)))
            throw new InvalidDataException("Incomplete user profile.");
        var loaded = document.RootElement.Deserialize<ProfileState>(Json) ?? throw new InvalidDataException("Empty user profile.");
        if (loaded.Version != 1 || loaded.PreferredName is null || loaded.Preferences is null || loaded.NameSource is null || loaded.PreferenceSource is null || loaded.PreferredName.Length > 80 || loaded.PreferredName.Any(char.IsControl) || loaded.NameSource.Length > 500 || loaded.PreferenceSource.Length > 500)
            throw new InvalidDataException("Invalid user profile.");
        return loaded with { PreferredName = MeaningfulFallback(loaded.PreferredName) };
    }

    private async Task SaveAsync(ProfileState next, bool keepBackup, CancellationToken cancellationToken)
    {
        var file = paths.Inside(RelativeFile);var temporary = paths.Inside(RelativeFile + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await using(var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, next, Json, cancellationToken);await stream.FlushAsync(cancellationToken);stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file)) File.Replace(temporary, file, keepBackup ? paths.Inside(RelativeFile + ".bak") : null);
            else File.Move(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ProfileState
    {
        public int Version { get; init; } = 1;
        public string PreferredName { get; init; } = "";
        public CommunicationPreferences Preferences { get; init; } = new();
        public string NameSource { get; init; } = "";
        public string PreferenceSource { get; init; } = "";
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
