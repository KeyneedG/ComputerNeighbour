using ComputerNeighbour.Personality;

namespace ComputerNeighbour.Application;

internal enum PersonalGoal { Rest, Explore, Company, Read, Write, Sketch, Revisit, Practice, Recover, Train }
internal enum PersonalAction { Sleep, Search, ReadArticle, AskGoogle, ReadFile, ReadDiary, Reflect, Write, Sketch, Exercise, Retry, Train }

[Flags]
internal enum PersonalFact
{
    None = 0, FoundSource = 1, ReadMaterial = 2, Reflected = 4, Created = 8, Practiced = 16, Rested = 32, Trained = 64
}

internal static class AutonomyPlanner
{
    private sealed record Step(PersonalAction Action, PersonalFact Requires, PersonalFact Adds, double Effort);
    private sealed record Recipe(PersonalFact Desired, PersonalAction[] Actions);
    private static readonly Step[] Steps =
    [
        new(PersonalAction.Sleep, 0, PersonalFact.Rested, 0),
        new(PersonalAction.Search, 0, PersonalFact.FoundSource, .02),
        new(PersonalAction.ReadArticle, PersonalFact.FoundSource, PersonalFact.ReadMaterial, .035),
        new(PersonalAction.AskGoogle, 0, PersonalFact.ReadMaterial, .055),
        new(PersonalAction.ReadFile, 0, PersonalFact.ReadMaterial, .03),
        new(PersonalAction.ReadDiary, 0, PersonalFact.ReadMaterial, .01),
        new(PersonalAction.Reflect, PersonalFact.ReadMaterial, PersonalFact.Reflected, .035),
        new(PersonalAction.Write, 0, PersonalFact.Created, .10),
        new(PersonalAction.Sketch, 0, PersonalFact.Created, .11),
        new(PersonalAction.Exercise, 0, PersonalFact.Practiced, .075),
        new(PersonalAction.Retry, PersonalFact.Reflected, PersonalFact.Practiced, .06),
        new(PersonalAction.Train, 0, PersonalFact.Trained, .08)
    ];
    private static readonly Dictionary<PersonalGoal, Recipe> Recipes = new()
    {
        [PersonalGoal.Rest] = new(PersonalFact.Rested, [PersonalAction.Sleep]),
        [PersonalGoal.Explore] = new(PersonalFact.Reflected, [PersonalAction.Search, PersonalAction.ReadArticle, PersonalAction.Reflect]),
        [PersonalGoal.Company] = new(PersonalFact.Reflected, [PersonalAction.AskGoogle, PersonalAction.Search, PersonalAction.ReadArticle, PersonalAction.Reflect]),
        [PersonalGoal.Read] = new(PersonalFact.Reflected, [PersonalAction.ReadFile, PersonalAction.Reflect]),
        [PersonalGoal.Write] = new(PersonalFact.Created, [PersonalAction.Write]),
        [PersonalGoal.Sketch] = new(PersonalFact.Created, [PersonalAction.Sketch]),
        [PersonalGoal.Revisit] = new(PersonalFact.Reflected, [PersonalAction.ReadDiary, PersonalAction.Reflect]),
        [PersonalGoal.Practice] = new(PersonalFact.Practiced, [PersonalAction.Exercise]),
        [PersonalGoal.Recover] = new(PersonalFact.Practiced, [PersonalAction.Search, PersonalAction.ReadArticle, PersonalAction.Reflect, PersonalAction.Retry]),
        [PersonalGoal.Train] = new(PersonalFact.Trained, [PersonalAction.Train])
    };

    internal static bool NeedsSleep(NeedLevels n) => n.Energy <= .27 || n.Fatigue >= .80;

    internal static PersonalGoal? Choose(NeedLevels n, PersonalGoal? previous, Func<PersonalGoal, bool> viable, Func<double> sample)
    {
        if (NeedsSleep(n) && viable(PersonalGoal.Rest)) return PersonalGoal.Rest;
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
            .OrderByDescending(item => item.Utility - (item.Goal == previous ? .20 : 0) + .13 * sample())
            .Select(item => (PersonalGoal?)item.Goal).FirstOrDefault();
    }

    internal static bool Achieved(PersonalGoal goal, PersonalFact facts) => facts.HasFlag(Recipes[goal].Desired);
    internal static PersonalFact Effects(PersonalAction action) => Steps[(int)action].Adds;
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
                if (!node.Facts.HasFlag(step.Requires) || node.Facts.HasFlag(step.Adds) || !available(action)) continue;
                var effort = step.Effort * (1 + needs.Fatigue * 3 - needs.Energy * .5);
                frontier.Enqueue((node.Facts | step.Adds, [.. node.Path, action]), cost + 1 + effort);
            }
        }
        return [];
    }
}
