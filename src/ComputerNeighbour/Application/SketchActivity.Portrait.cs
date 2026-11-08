using System.Text.RegularExpressions;

namespace ComputerNeighbour.Application;

public static partial class SketchActivity
{
    private static bool IsDefaultAnimePortrait(string subject) => Regex.IsMatch(subject,
        @"^(?:(?:a|an)\s+)?(?:(?:simple\s+)?anime(?:[- ]style)?\s+(?:face|portrait)|(?:простое\s+)?аниме[- ]лицо|лицо в стиле аниме)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static SketchPlan AnimePortrait()
    {
        const string ink = "#26213A", hair = "#4B375B", hairLight = "#725080";
        const string skin = "#FFE4CE", shade = "#E8AAAE", teal = "#2D686E", iris = "#5C81A7";
        return new("Anime portrait", "#F8EDE7",
        [
            // Backdrop, silhouette, and collar.
            Ellipse("#E8D9E9", 25, 7, 50, 86),
            Polygon(ink, (31,44), (32,25), (37,15), (44,11), (55,10), (63,14), (69,24),
                (71,39), (71,60), (74,78), (64,82), (35,82), (27,77), (31,63)),
            Polygon(hair, (32,46), (35,31), (64,29), (69,42), (69,64), (72,76), (62,79),
                (37,79), (29,76), (33,62)),
            Polygon(ink, (22,94), (26,84), (33,79), (44,75), (56,75), (67,79), (74,84), (78,94)),
            Polygon(teal, (24,94), (28,86), (34,81), (45,77), (55,77), (66,81), (72,86), (76,94)),
            Polygon("#DFEFEC", (39,80), (45,76), (55,76), (61,80), (56,89), (50,85), (44,89)),
            Polygon(ink, (44,65), (56,65), (57,78), (54,82), (50,84), (46,82), (43,78)),
            Polygon(shade, (45,65), (55,65), (56,78), (53,81), (50,82), (47,81), (44,78)),

            // Ears and face.
            Ellipse(ink, 33, 45, 8, 17),
            Ellipse(ink, 59, 45, 8, 17),
            Ellipse(shade, 34, 46, 7, 15),
            Ellipse(shade, 59, 46, 7, 15),
            Polygon(ink, (36,35), (39,26), (45,22), (55,22), (61,26), (64,35),
                (64,55), (61,64), (56,71), (51,75), (49,75), (44,71), (39,64), (36,55)),
            Polygon(skin, (37,35), (40,27), (45,23), (55,23), (60,27), (63,35),
                (63,55), (60,63), (55,70), (50,74), (45,70), (40,63), (37,55)),
            Ellipse("#F6BFC0", 39, 57, 6, 3),
            Ellipse("#F6BFC0", 55, 57, 6, 3),

            // Eyes, reflections, and expression.
            Ellipse(ink, 38, 43, 10, 13),
            Ellipse(ink, 52, 43, 10, 13),
            Ellipse("#FFF9F1", 39, 45, 8, 10),
            Ellipse("#FFF9F1", 53, 45, 8, 10),
            Ellipse(iris, 41, 44, 5, 11),
            Ellipse(iris, 54, 44, 5, 11),
            Ellipse(ink, 42, 45, 3, 7),
            Ellipse(ink, 55, 45, 3, 7),
            Ellipse("#FFFFFF", 41, 46, 2, 3),
            Ellipse("#FFFFFF", 54, 46, 2, 3),
            Polygon(ink, (39,41), (42,39), (46,40), (46,41), (42,41)),
            Polygon(ink, (54,40), (58,39), (61,41), (58,41), (54,41)),
            Polygon(shade, (49,55), (48,59), (51,60)),
            Polygon("#AC606C", (47,64), (50,65), (53,64), (52,66), (50,67), (48,66)),

            // Swept fringe and two enamel pins.
            Polygon(ink, (32,41), (34,24), (39,15), (46,12), (55,11), (62,15), (67,23),
                (69,39), (66,56), (63,51), (61,36), (57,32), (51,38), (45,39), (49,31),
                (40,37), (36,47), (34,59)),
            Polygon(hair, (33,41), (35,24), (40,16), (46,13), (55,12), (61,16), (66,23),
                (68,39), (66,53), (63,47), (62,35), (57,30), (51,36), (47,37), (51,29),
                (43,33), (39,37), (36,45), (34,55)),
            Polygon(hairLight, (36,30), (39,22), (45,17), (53,15), (60,17), (64,22), (56,19),
                (48,21), (42,25), (38,31)),
            Polygon("#FAD58B", (62,25), (66,29), (65,31), (61,27)),
            Polygon("#FAD58B", (61,29), (65,33), (64,35), (60,31)),
            Polygon("#D8A776", (24,31), (25,35), (28,36), (25,37), (24,41), (23,37), (20,36), (23,35)),
        ]);
    }

    private static SketchShape Ellipse(string color, int x, int y, int width, int height) =>
        new("ellipse", color, x, y, width, height);

    private static SketchShape Polygon(string color, params (int X, int Y)[] points) =>
        new("polygon", color, Points: points.Select(point => new[] { point.X, point.Y }).ToArray());

    private static SketchPlan SelfPortraitByWindow()
    {
        // Her character description calls out soft side light and an off-center detail.
        var portrait = AnimePortrait();
        List<SketchShape> layers =
        [
            new("rectangle", "#977D80", 6, 9, 33, 62),
            new("rectangle", "#BED0DB", 7, 11, 31, 57),
            Ellipse("#FFE7B3", 11, 19, 9, 13),
            new("rectangle", "#927681", 21, 11, 1, 57),
            new("rectangle", "#927681", 7, 37, 31, 2),
            new("rectangle", "#8E727B", 4, 67, 37, 4),
            Polygon("#F6D9BB", (7,69), (39,69), (57,97), (12,97)),
            .. portrait.Shapes.Skip(1).SkipLast(1),
            Polygon("#C8968E", (32,36), (35,24), (40,16), (45,14), (41,18), (37,25), (34,36)),
        ];
        return new("Self-portrait by the window", "#EEDFCF", layers);
    }
}
