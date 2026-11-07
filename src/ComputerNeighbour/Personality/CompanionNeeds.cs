using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Personality;

public sealed record NeedLevels
{
    public double Boredom { get; init; }
    public double Loneliness { get; init; }
    public double Hurt { get; init; }
    public DateTimeOffset? LastSketchAt { get; init; }

    internal bool IsValid=>new[]{Boredom,Loneliness,Hurt}.All(value=>double.IsFinite(value)&&value is >=0 and <=1);
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

    public void Advance(DateTimeOffset now,TimeSpan? idle,bool running)
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
    }

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
