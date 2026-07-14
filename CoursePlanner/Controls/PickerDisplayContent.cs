using CoursePlanner.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CoursePlanner.Controls;

internal enum PickerDisplayIcon
{
    Calendar,
    Clock
}

internal static class PickerDisplayContent
{
    private const double LeadingColumnWidth = 18;
    private const double LeadingIconSize = 17;
    private const double ChevronColumnWidth = 16;
    private const double ChevronIconSize = 12;
    private const double ColumnSpacing = 8;

    public static Grid Create(PickerDisplayIcon icon, string valueText)
    {
        var rowHeight = AppTypography.LineHeight(AppTextRole.PickerDisplay);
        var grid = new Grid
        {
            Height = rowHeight,
            ColumnSpacing = ColumnSpacing,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(LeadingColumnWidth) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(ChevronColumnWidth) }
            }
        };

        grid.Children.Add(CreateIconCell(IconGeometry(icon), LeadingColumnWidth, LeadingIconSize, rowHeight));

        var value = new TextBlock
        {
            Text = valueText,
            Style = AppTypography.TextStyle(AppTextRole.PickerDisplay),
            Height = rowHeight,
            LineHeight = rowHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var chevron = CreateIconCell(ChevronDownGeometry(), ChevronColumnWidth, ChevronIconSize, rowHeight);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        return grid;
    }

    private static Grid CreateIconCell(PathGeometry geometry, double width, double iconSize, double rowHeight)
    {
        var cell = new Grid
        {
            Width = width,
            Height = rowHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new PathIcon
        {
            Data = geometry,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        cell.Children.Add(new Viewbox
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform { Y = AppTypography.IconAlignmentOffset(AppTextRole.PickerDisplay, rowHeight) },
            Stretch = Stretch.Uniform,
            Child = icon
        });

        return cell;
    }

    private static PathGeometry IconGeometry(PickerDisplayIcon icon) =>
        icon == PickerDisplayIcon.Clock ? ClockGeometry() : CalendarGeometry();

    private static PathGeometry CalendarGeometry() =>
        new()
        {
            Figures =
            {
                Rect((3, 4), (17, 5.4)),
                Rect((3, 15.6), (17, 17)),
                Rect((3, 4), (4.4, 17)),
                Rect((15.6, 4), (17, 17)),
                Rect((5.2, 2), (6.6, 7)),
                Rect((13.4, 2), (14.8, 7)),
                Rect((6, 8), (8, 10)),
                Rect((9, 8), (11, 10)),
                Rect((12, 8), (14, 10)),
                Rect((6, 11.4), (8, 13.4)),
                Rect((9, 11.4), (11, 13.4)),
                Rect((12, 11.4), (14, 13.4))
            }
        };

    private static PathGeometry ClockGeometry() =>
        new()
        {
            FillRule = FillRule.EvenOdd,
            Figures =
            {
                Circle(center: (10, 10), radius: 8),
                Circle(center: (10, 10), radius: 6.4),
                Rect((9.25, 5.4), (10.75, 10.7)),
                Rect((9.9, 9.25), (14.2, 10.75))
            }
        };

    private static PathGeometry ChevronDownGeometry() =>
        new()
        {
            Figures =
            {
                Figure((4.2, 7), (5.2, 6), (10, 10.8), (14.8, 6), (15.8, 7), (10, 12.8))
            }
        };

    private static PathFigure Rect((double X, double Y) topLeft, (double X, double Y) bottomRight) =>
        Figure(topLeft, (bottomRight.X, topLeft.Y), bottomRight, (topLeft.X, bottomRight.Y));

    private static PathFigure Circle((double X, double Y) center, double radius)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - radius),
            IsClosed = true
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(center.X, center.Y + radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = true
        });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(center.X, center.Y - radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = true
        });
        return figure;
    }

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
