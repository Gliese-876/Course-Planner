using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CoursePlanner.Services;

public static class AppCommandIcons
{
    public static PathIcon NewCourse() => NewCourse(20);

    public static PathIcon NewCourse(double size) => Icon(NewCourseGeometry(), size);

    public static Viewbox NewCourseToolbar(double size) => ToolbarIcon(NewCourseGeometry(), size);

    public static PathIcon NewPlan() => NewPlan(20);

    public static PathIcon NewPlan(double size) => Icon(NewPlanGeometry(), size);

    public static Viewbox NewPlanToolbar(double size) => ToolbarIcon(NewPlanGeometry(), size);

    private static PathIcon Icon(PathGeometry geometry, double size) =>
        new()
        {
            Data = geometry,
            Width = size,
            Height = size
        };

    private static Viewbox ToolbarIcon(PathGeometry geometry, double size) =>
        new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = Icon(geometry, 20)
        };

    private static PathGeometry NewCourseGeometry() =>
        new()
        {
            Figures =
            {
                ThinPlusFigure(),
                Figure((12.5, 12.25), (16, 10.75), (19.5, 12.25), (16, 13.8)),
                Figure((13.7, 14.1), (16, 15.05), (18.3, 14.1), (18.3, 16.2), (17.4, 16.85), (16, 17.05), (14.6, 16.85), (13.7, 16.2)),
                Figure((18.8, 12.65), (19.6, 12.65), (19.6, 16.2), (18.8, 16.2))
            }
        };

    private static PathGeometry NewPlanGeometry() =>
        new()
        {
            Figures =
            {
                ThinPlusFigure(),
                Figure((13, 12), (18.2, 12), (19, 12.8), (19, 19), (12, 19), (12, 13), (13, 13)),
                Figure((14, 13.2), (17.5, 13.2), (17.5, 14.2), (14, 14.2)),
                Figure((14, 15.3), (17.5, 15.3), (17.5, 16.3), (14, 16.3))
            }
        };

    private static PathFigure ThinPlusFigure() =>
        Figure((8.5, 3), (9.5, 3), (9.5, 8.5), (15, 8.5), (15, 9.5), (9.5, 9.5), (9.5, 15), (8.5, 15), (8.5, 9.5), (3, 9.5), (3, 8.5), (8.5, 8.5));

    private static PathFigure Figure((double X, double Y) start, params (double X, double Y)[] points)
    {
        var figure = new PathFigure
        {
            StartPoint = Point(start),
            IsClosed = true
        };
        foreach (var point in points)
            figure.Segments.Add(new LineSegment { Point = Point(point) });
        return figure;
    }

    private static Point Point((double X, double Y) point) => new(point.X, point.Y);
}
