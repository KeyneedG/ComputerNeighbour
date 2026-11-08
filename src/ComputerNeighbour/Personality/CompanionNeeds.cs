using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Personality;

public sealed record NeedLevels
{
    public double Boredom { get; init; }
    public double Loneliness { get; init; }
    public double Hurt { get; init; }
    public double Energy { get; init; } = .76;
    public double Fatigue { get; init; } = .16;
    public double Curiosity { get; init; } = .60;
    public double Inspiration { get; init; } = .52;
    public double Excitement { get; init; } = .30;
    public double Focus { get; init; } = .62;
    public double Confidence { get; init; } = .50;
    public double Frustration { get; init; } = .10;
    public double Calm { get; init; } = .65;
    public double Novelty { get; init; } = .44;
    public double Reflection { get; init; } = .35;
    public double Persistence { get; init; } = .48;
    public double Playfulness { get; init; } = .52;
    public double Ambition { get; init; } = .50;
    public double Contentment { get; init; } = .54;
    public DateTimeOffset? LastSketchAt { get; init; }

    internal bool IsValid => new[] { Boredom, Loneliness, Hurt, Energy, Fatigue, Curiosity, Inspiration, Excitement,
        Focus, Confidence, Frustration, Calm, Novelty, Reflection, Persistence, Playfulness, Ambition, Contentment }
        .All(value => double.IsFinite(value) && value is >= 0 and <= 1);
}

internal sealed class CompanionNeeds(NeedsOptions options,NeedLevels initial)
{
    private const double Threshold=.75;
    private DateTimeOffset? lastTick;
    private DateTimeOffset? invitation;
    private DateTimeOffset? lastSketchAttempt;
    private bool feltOverlooked;
    public NeedLevels Levels { get; private set; }=initial;
    public bool WaitingForReply=>invitation.HasValue;

    public void Advance(DateTimeOffset now,TimeSpan? idle,bool running,bool personalLife=false,bool resting=false)
    {
        var elapsed=lastTick is {} last?Math.Clamp((now-last).TotalMinutes,0,.5):0;
        lastTick=now;
        if(!options.Enabled||!running)return;
        var hurt=Math.Max(0,Levels.Hurt-elapsed/50);
        if(invitation is {} asked&&!feltOverlooked&&now-asked>=TimeSpan.FromMinutes(options.InvitationPatienceMinutes)
            &&IsPresent(idle)&&now-idle!.Value>asked)
        {
            // Recent input is a clue about the session, never about the person's identity or intent.
            feltOverlooked=true;
            hurt=Math.Max(hurt,.35);
        }
        Levels=Levels with
        {
            Boredom=Math.Min(1,Levels.Boredom+elapsed*Threshold/options.BoredomMinutes),
            Loneliness=Math.Min(1,Levels.Loneliness+elapsed*Threshold/options.LonelinessMinutes),
            Hurt=hurt
        };
        if (!personalLife || resting) return;
        Levels = Levels with
        {
            Energy = Bound(Levels.Energy - .008 * elapsed),
            Fatigue = Bound(Levels.Fatigue + .009 * elapsed),
            Curiosity = Toward(Levels.Curiosity, .78, .025 * elapsed),
            Inspiration = Toward(Levels.Inspiration, .66, .020 * elapsed),
            Excitement = Toward(Levels.Excitement, .24, .032 * elapsed),
            Focus = Toward(Levels.Focus, .65, .024 * elapsed),
            Confidence = Toward(Levels.Confidence, .52, .012 * elapsed),
            Frustration = Bound(Levels.Frustration - .04 * elapsed),
            Calm = Toward(Levels.Calm, .65, .026 * elapsed),
            Novelty = Bound(Levels.Novelty + .017 * elapsed),
            Reflection = Bound(Levels.Reflection + .010 * elapsed),
            Persistence = Toward(Levels.Persistence, .50, .014 * elapsed),
            Playfulness = Toward(Levels.Playfulness, .60, .016 * elapsed),
            Ambition = Bound(Levels.Ambition + .014 * elapsed),
            Contentment = Toward(Levels.Contentment, .52, .018 * elapsed)
        };
    }

    public void NotePersonalStep(double effort, bool discovery = false, bool creative = false, bool social = false,
        bool reflected = false, bool practiced = false, bool failed = false, bool milestone = true)
    {
        if (!milestone)
        {
            Levels = Levels with { Energy = Bound(Levels.Energy - effort), Fatigue = Bound(Levels.Fatigue + effort * .8),
                Focus = Bound(Levels.Focus - effort / 2) };
            return;
        }
        Levels = Levels with
        {
            Energy = Bound(Levels.Energy - effort),
            Fatigue = Bound(Levels.Fatigue + effort * .8),
            Boredom = Bound(Levels.Boredom - (failed ? 0 : .16)),
            Loneliness = Bound(Levels.Loneliness - (social ? .38 : 0)),
            Curiosity = Bound(Levels.Curiosity + (discovery ? -.16 : .02)),
            Inspiration = Bound(Levels.Inspiration + (creative ? -.20 : discovery ? .13 : 0)),
            Excitement = Bound(Levels.Excitement + (failed ? -.12 : creative || discovery ? .14 : .04)),
            Focus = Bound(Levels.Focus - effort / 2),
            Confidence = Bound(Levels.Confidence + (failed ? -.09 : .055)),
            Frustration = Bound(Levels.Frustration + (failed ? .22 : -.04)),
            Calm = Bound(Levels.Calm + (reflected ? .16 : failed ? -.09 : -.025)),
            Novelty = Bound(Levels.Novelty - (discovery ? .24 : .05)),
            Reflection = Bound(Levels.Reflection + (reflected ? -.30 : .045)),
            Persistence = Bound(Levels.Persistence + (failed ? .12 : -.04)),
            Playfulness = Bound(Levels.Playfulness + (creative ? -.19 : .035)),
            Ambition = Bound(Levels.Ambition - (practiced ? .28 : 0)),
            Contentment = Bound(Levels.Contentment + (failed ? -.08 : .10))
        };
    }

    public void NoteRest() => Levels = Levels with
    {
        Energy = Math.Max(Levels.Energy, .86), Fatigue = Math.Min(Levels.Fatigue, .14),
        Focus = Math.Max(Levels.Focus, .72), Frustration = Levels.Frustration * .45, Calm = .80,
        Inspiration = Bound(Levels.Inspiration + .08), Playfulness = Bound(Levels.Playfulness + .09)
    };

    private static double Bound(double value) => Math.Clamp(value, 0, 1);
    private static double Toward(double value, double target, double amount) => value + (target - value) * amount;

    public bool WantsCompany(TimeSpan? idle)=>options.Enabled&&options.InviteWhenLonely&&!WaitingForReply&&
        Levels.Loneliness>=Threshold&&IsPresent(idle);

    public bool WantsToSketch(DateTimeOffset now,TimeSpan? idle)=>options.Enabled&&options.DrawWhenBored
        &&Levels.Boredom>=Threshold&&idle>=TimeSpan.FromSeconds(options.SketchIdleSeconds)
        &&(Levels.LastSketchAt is not {} drawn||now-drawn>=TimeSpan.FromMinutes(options.SketchCooldownMinutes))
        &&(lastSketchAttempt is not {} tried||now-tried>=TimeSpan.FromMinutes(5));

    public void NoteInvitation(DateTimeOffset now){invitation=now;feltOverlooked=false;}
    public void NoteSketchAttempt(DateTimeOffset now)=>lastSketchAttempt=now;
    public void NoteSketch(DateTimeOffset now)=>Levels=Levels with {Boredom=.12,LastSketchAt=now};

    public void NoteConversation(DateTimeOffset now)
    {
        if(!options.Enabled)return;
        lastTick=now;
        invitation=null;
        feltOverlooked=false;
        Levels=Levels with {Boredom=Levels.Boredom*.35,Loneliness=Levels.Loneliness*.35,Hurt=Levels.Hurt*.35};
    }

    private bool IsPresent(TimeSpan? idle)=>idle is {} value&&value<TimeSpan.FromSeconds(options.RecentInputSeconds);
}
