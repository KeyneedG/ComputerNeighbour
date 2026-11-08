using ComputerNeighbour.Personality;

namespace ComputerNeighbour.Application;

internal enum PersonalGoal { Rest, Explore, Company, Read, Write, Sketch, Revisit, Practice, Recover, Train, ContinueStory, NameEntry }
internal enum PersonalAction
{
    Sleep, Search, ReadArticle, AskGoogle, ReadFile, ReadDiary, Reflect, Write, Sketch, Exercise, Retry, Train,
    Plan, Outline, Opening, TurningPoint, Ending, DrawDraft, Review, Revise, SaveNote, SaveExercise,
    LoadStory, ContinueStory, ReserveTitle, TitleEntry, RenameEntry, PlanContinuation,
    InspectImage, ReflectOnWeb, SaveDraft, ReviseStory
}

[Flags]
internal enum PersonalFact
{
    None = 0, FoundSource = 1, ReadMaterial = 2, Reflected = 4, Created = 8, Practiced = 16, Rested = 32, Trained = 64,
    Planned = 128, Opening = 256, Middle = 512, Drafted = 1024, Reviewed = 2048, Accepted = 4096, Reserved = 8192,
    ImagesConsidered = 16384, DraftPending = 32768
}

internal static class AutonomyPlanner
{
    private sealed record Step(PersonalFact Requires, PersonalFact Adds, double Effort,
        PersonalFact Clears = 0, PersonalFact Forbids = 0)
    {
        internal PersonalFact Apply(PersonalFact facts) => (facts & ~Clears) | Adds;
    }
    private sealed record Recipe(PersonalFact Desired, PersonalAction[] Actions);
    private static readonly Step[] Steps =
    [
        new(0, PersonalFact.Rested, 0), // Sleep
        new(PersonalFact.Planned, PersonalFact.FoundSource, .02), // Search
        new(PersonalFact.Planned | PersonalFact.FoundSource, PersonalFact.ReadMaterial, .03), // ReadArticle
        new(PersonalFact.Planned, PersonalFact.ReadMaterial, .05), // AskGoogle
        new(PersonalFact.Planned, PersonalFact.ReadMaterial, .02), // ReadFile
        new(PersonalFact.Planned, PersonalFact.ReadMaterial, .01), // ReadDiary
        new(PersonalFact.Planned | PersonalFact.ReadMaterial, PersonalFact.Drafted, .03), // Reflect
        new(PersonalFact.Reserved, PersonalFact.Created, .01), // Write
        new(PersonalFact.Accepted, PersonalFact.Created, .02), // Sketch
        new(PersonalFact.Planned, PersonalFact.Drafted, .035), // Exercise
        new(PersonalFact.Accepted, PersonalFact.Practiced, .04), // Retry
        new(PersonalFact.Planned, PersonalFact.Trained, .08), // Train
        new(0, PersonalFact.Planned, .02),
        new(0, PersonalFact.Planned, .03), // Outline
        new(PersonalFact.Planned, PersonalFact.Opening | PersonalFact.DraftPending, .04, Forbids: PersonalFact.Opening | PersonalFact.DraftPending),
        new(PersonalFact.Opening, PersonalFact.Middle | PersonalFact.DraftPending, .04, Forbids: PersonalFact.Middle | PersonalFact.DraftPending),
        new(PersonalFact.Middle, PersonalFact.Drafted | PersonalFact.DraftPending, .04, Forbids: PersonalFact.Drafted | PersonalFact.DraftPending),
        new(PersonalFact.Planned, PersonalFact.Drafted, .07), // DrawDraft
        new(PersonalFact.Drafted, PersonalFact.Reviewed | PersonalFact.Accepted, .025, Forbids: PersonalFact.Reviewed | PersonalFact.DraftPending),
        new(PersonalFact.Reviewed, 0, .045, Clears: PersonalFact.Reviewed, Forbids: PersonalFact.Accepted),
        new(PersonalFact.Accepted, PersonalFact.Reflected, .01),
        new(PersonalFact.Accepted, PersonalFact.Practiced, .01),
        new(0, PersonalFact.ReadMaterial, .01), // LoadStory
        new(PersonalFact.ReadMaterial | PersonalFact.Planned, PersonalFact.Drafted, .045), // ContinueStory
        new(PersonalFact.Accepted, PersonalFact.Reserved, .01), // ReserveTitle
        new(0, PersonalFact.Reserved, .025), // TitleEntry
        new(PersonalFact.Reserved, PersonalFact.Created, .01), // RenameEntry
        new(PersonalFact.ReadMaterial, PersonalFact.Planned, .02), // PlanContinuation
        new(PersonalFact.ReadMaterial, PersonalFact.ImagesConsidered, .025),
        new(PersonalFact.Planned | PersonalFact.ReadMaterial | PersonalFact.ImagesConsidered, PersonalFact.Drafted, .03),
        new(PersonalFact.DraftPending, 0, .005, Clears: PersonalFact.DraftPending),
        new(PersonalFact.Reviewed, PersonalFact.DraftPending, .045, Clears: PersonalFact.Reviewed, Forbids: PersonalFact.Accepted)
    ];
    private static readonly Dictionary<PersonalGoal, Recipe> Recipes = new()
    {
        [PersonalGoal.Rest] = new(PersonalFact.Rested, [PersonalAction.Sleep]),
        [PersonalGoal.Explore] = WebRecipe(PersonalAction.Search, PersonalAction.ReadArticle),
        [PersonalGoal.Company] = WebRecipe(PersonalAction.AskGoogle, PersonalAction.Search, PersonalAction.ReadArticle),
        [PersonalGoal.Read] = NoteRecipe(PersonalAction.ReadFile),
        [PersonalGoal.Write] = new(PersonalFact.Created, [PersonalAction.Outline, PersonalAction.Opening, PersonalAction.TurningPoint,
            PersonalAction.Ending, PersonalAction.SaveDraft, PersonalAction.Review, PersonalAction.ReviseStory, PersonalAction.ReserveTitle, PersonalAction.Write]),
        [PersonalGoal.Sketch] = new(PersonalFact.Created, [PersonalAction.Plan, PersonalAction.DrawDraft, PersonalAction.Review,
            PersonalAction.Revise, PersonalAction.Sketch]),
        [PersonalGoal.Revisit] = NoteRecipe(PersonalAction.ReadDiary),
        [PersonalGoal.Practice] = new(PersonalFact.Practiced, [PersonalAction.Plan, PersonalAction.Exercise, PersonalAction.Review,
            PersonalAction.Revise, PersonalAction.SaveExercise]),
        [PersonalGoal.Recover] = new(PersonalFact.Practiced, [PersonalAction.Plan, PersonalAction.Search, PersonalAction.ReadArticle,
            PersonalAction.Reflect, PersonalAction.Review, PersonalAction.Revise, PersonalAction.Retry]),
        [PersonalGoal.Train] = new(PersonalFact.Trained, [PersonalAction.Plan, PersonalAction.Train]),
        [PersonalGoal.ContinueStory] = new(PersonalFact.Created, [PersonalAction.LoadStory, PersonalAction.PlanContinuation, PersonalAction.ContinueStory,
            PersonalAction.Review, PersonalAction.Revise, PersonalAction.ReserveTitle, PersonalAction.Write]),
        [PersonalGoal.NameEntry] = new(PersonalFact.Created, [PersonalAction.TitleEntry, PersonalAction.RenameEntry])
    };

    private static Recipe NoteRecipe(params PersonalAction[] acquire) => new(PersonalFact.Reflected,
        [PersonalAction.Plan, .. acquire, PersonalAction.Reflect, PersonalAction.Review, PersonalAction.Revise, PersonalAction.SaveNote]);
    private static Recipe WebRecipe(params PersonalAction[] acquire) => new(PersonalFact.Reflected,
        [PersonalAction.Plan, .. acquire, PersonalAction.InspectImage, PersonalAction.ReflectOnWeb,
            PersonalAction.Review, PersonalAction.Revise, PersonalAction.SaveNote]);

    internal static bool NeedsSleep(NeedLevels n) => n.Energy <= .27 || n.Fatigue >= .80;

    internal static bool ResearchIsDue(IReadOnlyList<PersonalGoal> recent) =>
        recent.Count >= 3 && recent.TakeLast(3).All(goal => goal is not (PersonalGoal.Explore or PersonalGoal.Company or PersonalGoal.Recover));

    internal static PersonalGoal? Choose(NeedLevels n, PersonalGoal? previous, Func<PersonalGoal, bool> viable, Func<double> sample,
        IReadOnlyList<PersonalGoal>? recent = null)
    {
        if (NeedsSleep(n) && viable(PersonalGoal.Rest)) return PersonalGoal.Rest;
        if (recent is not null && ResearchIsDue(recent))
        {
            if (viable(PersonalGoal.Company) && n.Loneliness >= .75) return PersonalGoal.Company;
            if (viable(PersonalGoal.Explore)) return PersonalGoal.Explore;
        }
        var ready = n.Energy * (1 - n.Fatigue * .6);
        (PersonalGoal Goal, double Utility)[] goals =
        [
            (PersonalGoal.Explore, .34*n.Curiosity + .24*n.Novelty + .21*n.Boredom + .12*n.Excitement),
            (PersonalGoal.Company, .75*n.Loneliness + .12*n.Curiosity + .10*n.Playfulness - .15*n.Hurt),
            (PersonalGoal.Read, .24*n.Boredom + .29*n.Curiosity + .17*n.Focus + .15*n.Calm),
            (PersonalGoal.Write, .38*n.Inspiration + .24*ready + .20*n.Focus + .14*n.Excitement),
            (PersonalGoal.Sketch, .39*n.Inspiration + .24*ready + .22*n.Playfulness + .15*n.Boredom),
            (PersonalGoal.Revisit, .39*n.Reflection + .21*n.Calm + .18*n.Frustration + .14*(1-n.Contentment)),
            (PersonalGoal.Practice, .34*n.Ambition + .25*n.Persistence + .17*n.Confidence + .19*ready),
            (PersonalGoal.Recover, .28 + .23*n.Persistence + .24*n.Ambition + .18*n.Focus - .16*n.Frustration),
            (PersonalGoal.Train, .36 + .24*n.Ambition + .14*n.Confidence + .21*ready)
        ];
        return goals.Where(item => viable(item.Goal))
            .OrderByDescending(item => item.Utility - (item.Goal == previous ? .20 : 0) -
                .14 * (recent?.TakeLast(5).Count(goal => goal == item.Goal) ?? 0) + .13 * sample())
            .Select(item => (PersonalGoal?)item.Goal).FirstOrDefault();
    }

    internal static bool Achieved(PersonalGoal goal, PersonalFact facts) => facts.HasFlag(Recipes[goal].Desired);
    internal static PersonalFact Advance(PersonalAction action, PersonalFact known, bool accepted = true)
    {
        var result = Steps[(int)action].Apply(known);
        return action == PersonalAction.Review && !accepted ? result & ~PersonalFact.Accepted : result;
    }
    internal static double Effort(PersonalAction action) => Steps[(int)action].Effort;

    internal static IReadOnlyList<PersonalAction> Plan(PersonalGoal goal, PersonalFact known, NeedLevels needs,
        Func<PersonalAction, bool> available)
    {
        var recipe = Recipes[goal];
        var frontier = new PriorityQueue<(PersonalFact Facts, PersonalAction[] Path), double>();
        var visited = new HashSet<PersonalFact>();
        frontier.Enqueue((known, []), 0);
        while (frontier.TryDequeue(out var node, out var cost))
        {
            if (!visited.Add(node.Facts)) continue;
            if (node.Facts.HasFlag(recipe.Desired)) return node.Path;
            foreach (var action in recipe.Actions)
            {
                var step = Steps[(int)action];
                var result = step.Apply(node.Facts);
                if (!node.Facts.HasFlag(step.Requires) || (node.Facts & step.Forbids) != 0 ||
                    result == node.Facts || !available(action)) continue;
                var effort = step.Effort * (1 + needs.Fatigue * 3 - needs.Energy * .5);
                frontier.Enqueue((result, [.. node.Path, action]), cost + 1 + effort);
            }
        }
        return [];
    }
}
