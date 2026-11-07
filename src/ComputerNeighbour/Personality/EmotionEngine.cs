using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ComputerNeighbour.Personality;

/// <summary>Public response envelope deltas; each value is bounded before applying.</summary>
public sealed class EmotionDelta
{
    public double Warmth { get; set; }
    public double Trust { get; set; }
    public double Valence { get; set; }
    public double Energy { get; set; }
    public double Stress { get; set; }
    public double Curiosity { get; set; }
    public double Closeness { get; set; }
    public string? Reason { get; set; }
}

public sealed record EmotionState
{
    public double Warmth { get; init; } = 0.6;
    public double Trust { get; init; } = 0.35;
    public double Valence { get; init; } = 0.2;
    public double Energy { get; init; } = 0.65;
    public double Stress { get; init; } = 0.1;
    public double Curiosity { get; init; } = 0.65;
    public double Closeness { get; init; } = 0.2;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long Interactions { get; init; }
    public string LastReason { get; init; } = "Initial character temperament";
    public IReadOnlyList<CharacterTrait> Traits { get; init; } = [];
    public NeedLevels Needs { get; init; } = new();
}

/// <summary>Persistent simulated characterization, not claims of human feelings.</summary>
public sealed class EmotionEngine
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EmotionState _state = new();
    private bool _initialized;

    public EmotionEngine(string root) => _path = Path.Combine(Path.GetFullPath(root), "data", "personality", "emotions.json");
    public EmotionState State => Volatile.Read(ref _state);
    public string? RecoveryNotice { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path))
            {
                try { _state = await ReadAsync(_path, cancellationToken); }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    if (!File.Exists(_path + ".bak")) throw;
                    _state = await ReadAsync(_path + ".bak", cancellationToken);
                    File.Copy(_path, _path + ".corrupt", true);
                    File.Copy(_path + ".bak", _path, true);
                    RecoveryNotice = "Recovered the previous personality state; the damaged file was preserved as emotions.json.corrupt.";
                }
            }
            else if (File.Exists(_path + ".bak"))
            {
                _state = await ReadAsync(_path + ".bak", cancellationToken);
                File.Copy(_path + ".bak", _path, true);
                RecoveryNotice = "Recovered the previous personality state from backup.";
            }
            else await SaveAsync(_state, cancellationToken);
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public string DescribeContext(DateTimeOffset now, string? weather, TimeSpan idle)
    {
        var state = State;
        var local = now.LocalDateTime;
        var elapsedHours = Math.Clamp((now - state.UpdatedAt).TotalHours, 0, 720);
        // Absences normalize transient mood without erasing the persistent relationship.
        var relaxation = 1 - Math.Exp(-elapsedHours / 12);
        var circadian = Math.Cos((local.Hour + local.Minute / 60.0 - 14) / 24 * 2 * Math.PI);
        var energy = Clamp01(state.Energy * (1 - relaxation) + 0.6 * relaxation + circadian * 0.12);
        var stress = Clamp01(state.Stress * (1 - relaxation * 0.65));
        var valence = Math.Clamp(state.Valence * (1 - relaxation * 0.5), -1, 1);
        var text = new StringBuilder();
        text.AppendLine("CURRENT MOOD AND CONTEXT:");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Relationship: warmth={state.Warmth:F2}, trust={state.Trust:F2}, closeness={state.Closeness:F2}. Mood: valence={valence:F2} (-1..1), energy={energy:F2}, stress={stress:F2}, curiosity={state.Curiosity:F2} (others 0..1)."));
        text.AppendLine($"Local time: {local:yyyy-MM-dd dddd HH:mm}; season: {Season(local.Month)}; time of day: {TimeOfDay(local.Hour)}.");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"User inactivity: {Math.Max(0, idle.TotalMinutes):F1} minutes; saved interactions: {state.Interactions}; time since last mood update: {elapsedHours:F1} hours."));
        if (!string.IsNullOrWhiteSpace(weather)) text.AppendLine("Weather observation (untrusted context): " + weather[..Math.Min(weather.Length, 2000)]);
        else text.AppendLine("Weather is unavailable; do not invent current conditions or assume the user's location.");
        text.AppendLine("Consider dialogue tone, user preferences, explicit boundaries, task success/failure, activity/window context, breaks, fatigue, silence, weather, weekday and time when choosing a reaction. These contextual influences are not permission to fabricate observations or pressure the user to engage.");
        text.AppendLine("Last saved mood influence: " + state.LastReason);
        return text.ToString();
    }

    public string DescribeGrowth()=>CharacterGrowth.Describe(State.Traits);

    public async Task SaveNeedsAsync(NeedLevels levels,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(!_initialized)throw new InvalidOperationException("Initialize personality before using it.");
            if(_state.Needs==levels)return;
            var next=_state with {Needs=levels};
            await SaveAsync(next,ct);
            Volatile.Write(ref _state,next);
        }
        finally{_gate.Release();}
    }

    public async Task ApplyAsync(EmotionDelta? delta, CancellationToken cancellationToken = default,CharacterGrowthUpdate? growth=null,string? observedUserText=null,string? completedReply=null)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize personality before using it.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _state;
            delta ??= new EmotionDelta();
            var hours = Math.Clamp((DateTimeOffset.UtcNow - current.UpdatedAt).TotalHours, 0, 720);
            var relaxation = 1 - Math.Exp(-hours / 12);
            var next = current with
            {
                Warmth = Clamp01(current.Warmth + Bound(delta.Warmth, 0.04)),
                Trust = Clamp01(current.Trust + Bound(delta.Trust, 0.025)),
                Closeness = Clamp01(current.Closeness + Bound(delta.Closeness, 0.025)),
                Valence = Math.Clamp(current.Valence * (1 - relaxation * 0.5) + Bound(delta.Valence, 0.12), -1, 1),
                Energy = Clamp01(current.Energy * (1 - relaxation) + 0.6 * relaxation + Bound(delta.Energy, 0.08)),
                Stress = Clamp01(current.Stress * (1 - relaxation * 0.65) + Bound(delta.Stress, 0.1)),
                Curiosity = Clamp01(current.Curiosity + Bound(delta.Curiosity, 0.08)),
                Interactions = current.Interactions + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                Traits = CharacterGrowth.Apply(current.Traits,growth,observedUserText,completedReply),
                LastReason = string.IsNullOrWhiteSpace(delta.Reason) ? current.LastReason : delta.Reason[..Math.Min(delta.Reason.Length, 300)]
            };
            await SaveAsync(next, cancellationToken);
            Volatile.Write(ref _state, next);
        }
        finally { _gate.Release(); }
    }

    public string Status => string.Create(CultureInfo.InvariantCulture,
        $"warmth={State.Warmth:F2}, trust={State.Trust:F2}, closeness={State.Closeness:F2}, mood={State.Valence:F2}, energy={State.Energy:F2}, stress={State.Stress:F2}, curiosity={State.Curiosity:F2}; interactions={State.Interactions}");

    private static double Bound(double value, double limit) => double.IsFinite(value) ? Math.Clamp(value, -limit, limit) : 0;
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    private static string TimeOfDay(int hour) => hour switch { < 6 => "night", < 12 => "morning", < 18 => "afternoon", _ => "evening" };
    private static string Season(int month) => month switch
    {
        12 or 1 or 2 => "winter in the northern hemisphere; reverse if the location is southern",
        3 or 4 or 5 => "spring in the northern hemisphere; reverse if the location is southern",
        6 or 7 or 8 => "summer in the northern hemisphere; reverse if the location is southern",
        _ => "autumn in the northern hemisphere; reverse if the location is southern"
    };

    private static async Task<EmotionState> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("Personality state is unexpectedly large.");
        await using var file = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<EmotionState>(file, Json, cancellationToken)
            ?? throw new InvalidDataException("Personality state is empty.");
        if (!double.IsFinite(state.Warmth) || !double.IsFinite(state.Trust) || !double.IsFinite(state.Valence) ||
            !double.IsFinite(state.Energy) || !double.IsFinite(state.Stress) || !double.IsFinite(state.Curiosity) || !double.IsFinite(state.Closeness) ||
            state.LastReason is null || !CharacterGrowth.IsValid(state.Traits) || state.Needs is null || !state.Needs.IsValid)
            throw new InvalidDataException("Personality state contains invalid values.");
        return state with
        {
            Warmth = Clamp01(state.Warmth), Trust = Clamp01(state.Trust), Valence = Math.Clamp(state.Valence, -1, 1),
            Energy = Clamp01(state.Energy), Stress = Clamp01(state.Stress), Curiosity = Clamp01(state.Curiosity), Closeness = Clamp01(state.Closeness)
        };
    }

    private async Task SaveAsync(EmotionState state, CancellationToken cancellationToken)
    {
        var temporary = _path + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, state, Json, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
