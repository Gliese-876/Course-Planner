using CoursePlanner.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace CoursePlanner.Controls;

public sealed class CourseLibraryCourseEventArgs : EventArgs
{
    public CourseLibraryCourseEventArgs(CourseLibraryTreeCourse course, FrameworkElement target, Windows.Foundation.Point position)
    {
        Course = course;
        Target = target;
        Position = position;
    }

    public CourseLibraryTreeCourse Course { get; }
    public string OfferingId => Course.OfferingId;
    public FrameworkElement Target { get; }
    public Windows.Foundation.Point Position { get; }
}

public sealed class CourseLibraryTree : UserControl
{
    private const double IndentSize = 16;
    private const double StatusIconSize = 12;
    private const double NativeStatusIconSize = 20;

    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _panel = new() { Spacing = 0 };
    private readonly HashSet<string> _collapsedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<CourseLibraryTreeGroup> _groups = Array.Empty<CourseLibraryTreeGroup>();

    public CourseLibraryTree()
    {
        AppTypography.Apply(this);
        _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _panel
        };
        _scrollViewer.SizeChanged += (_, args) => _panel.MinWidth = args.NewSize.Width;
        Content = _scrollViewer;
        IsTabStop = true;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public string TreeAutomationId
    {
        get => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(_scrollViewer);
        set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_scrollViewer, value);
    }

    public bool AllowCourseDrag { get; set; }

    public string CourseRowAutomationId { get; set; } = "LibraryCourseRow";

    public string CourseStatusAutomationName { get; set; } = "";

    public string EmptyText { get; set; } = "";

    public event EventHandler<CourseLibraryCourseEventArgs>? CourseInvoked;
    public event EventHandler<CourseLibraryCourseEventArgs>? CourseContextRequested;
    public event EventHandler<CourseLibraryCourseEventArgs>? CourseStatusDotTapped;

    public void SetGroups(IEnumerable<CourseLibraryTreeGroup> groups)
    {
        _groups = groups.ToList();
        Rebuild();
    }

    public void Rebuild()
    {
        _panel.Children.Clear();
        if (_groups.Count == 0 || _groups.All(group => group.Courses.Count == 0))
        {
            AddEmptyState();
            return;
        }

        foreach (var semesterGroup in _groups.GroupBy(x => x.SemesterName))
        {
            var semesterKey = $"semester:{semesterGroup.Key}";
            AddFolderRow(semesterGroup.Key, semesterKey, 0);
            if (IsCollapsed(semesterKey))
                continue;

            foreach (var group in semesterGroup.GroupBy(x => x.CourseGroupTypeText))
            {
                var groupKey = $"{semesterKey}|group:{group.Key}";
                AddFolderRow(group.Key, groupKey, 1);
                if (IsCollapsed(groupKey))
                    continue;

                foreach (var study in group.GroupBy(x => x.StudyTypeText))
                {
                    var studyKey = $"{groupKey}|study:{study.Key}";
                    AddFolderRow(study.Key, studyKey, 2);
                    if (IsCollapsed(studyKey))
                        continue;

                    foreach (var course in study.SelectMany(x => x.Courses))
                        AddCourseRow(course, 3);
                }
            }
        }
    }

    private void AddEmptyState()
    {
        var text = AppTypography.TextBlock(EmptyText, AppTextRole.Body, TextWrapping.Wrap);
        text.Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextSecondary, Colors.Gray);
        text.Margin = new Thickness(12, 16, 12, 16);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(text, "LibraryTreeEmptyState");
        _panel.Children.Add(text);
    }

    private bool IsCollapsed(string key) => _collapsedKeys.Contains(key);

    private void Toggle(string key)
    {
        if (!_collapsedKeys.Add(key))
            _collapsedKeys.Remove(key);
        Rebuild();
    }

    private void AddFolderRow(string text, string key, int level)
    {
        var expanded = !IsCollapsed(key);
        var row = CreateRowButton(level, minHeight: 32);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, "LibraryTreeFolderRow");
        row.Click += (_, _) => Toggle(key);

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(18) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 6
        };
        grid.Children.Add(new FontIcon
        {
            Glyph = expanded ? "\uE70D" : "\uE76C",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new TextBlock
        {
            Text = text,
            Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        row.Content = grid;
        _panel.Children.Add(row);
    }

    private void AddCourseRow(CourseLibraryTreeCourse item, int level)
    {
        var row = CreateRowButton(level, minHeight: 44);
        row.Tag = item.OfferingId;
        row.CanDrag = AllowCourseDrag;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, CourseRowAutomationId);
        ToolTipService.SetToolTip(row, item.Summary);
        row.Click += (_, _) => CourseInvoked?.Invoke(this, new CourseLibraryCourseEventArgs(item, row, new Windows.Foundation.Point(0, 0)));
        row.RightTapped += (_, e) =>
        {
            CourseContextRequested?.Invoke(this, new CourseLibraryCourseEventArgs(item, row, e.GetPosition(row)));
            e.Handled = true;
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CourseInvoked?.Invoke(this, new CourseLibraryCourseEventArgs(item, row, new Windows.Foundation.Point(0, 0)));
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Application)
            {
                CourseContextRequested?.Invoke(this, new CourseLibraryCourseEventArgs(item, row, new Windows.Foundation.Point(24, 24)));
                e.Handled = true;
            }
        };
        row.DragStarting += (_, args) =>
        {
            if (!AllowCourseDrag)
                return;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetText(item.OfferingId);
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(18) },
                new ColumnDefinition { Width = new GridLength(14) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 5
        };

        var status = item.Status;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            row,
            status.Kind == CourseLibraryStatusKind.None
                ? item.CourseName
                : $"{item.CourseName}, {status.Text}");
        if (status.Kind != CourseLibraryStatusKind.None)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(row, status.Text);

        var statusIndicator = new Viewbox
        {
            Tag = item.OfferingId,
            Width = StatusIconSize,
            Height = StatusIconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = new SymbolIcon
            {
                Width = NativeStatusIconSize,
                Height = NativeStatusIconSize,
                Symbol = SymbolForStatus(status.Kind),
                Foreground = BrushForStatus(status.Kind)
            },
            Visibility = status.Kind == CourseLibraryStatusKind.None ? Visibility.Collapsed : Visibility.Visible
        };
        AppAnimationLayer.SetProfile(statusIndicator, AppAnimationProfile.Interactive);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(statusIndicator, "CoursePlanStatusIndicator");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(statusIndicator, status.Text);
        ToolTipService.SetToolTip(statusIndicator, status.Text);
        statusIndicator.Tapped += (_, e) =>
        {
            if (status.Kind == CourseLibraryStatusKind.None)
                return;
            CourseStatusDotTapped?.Invoke(this, new CourseLibraryCourseEventArgs(item, statusIndicator, e.GetPosition(statusIndicator)));
            e.Handled = true;
        };
        Grid.SetColumn(statusIndicator, 1);
        grid.Children.Add(statusIndicator);

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = item.CourseName,
            Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Summary,
            Style = AppTypography.TextStyle(AppTextRole.Body),
            Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextSecondary, Colors.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        row.Content = grid;
        _panel.Children.Add(row);
    }

    private static Button CreateRowButton(int level, double minHeight)
    {
        return new Button
        {
            MinHeight = minHeight,
            Padding = new Thickness(6 + level * IndentSize, 3, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Background = AppBrushes.Transparent()
        };
    }

    private Brush BrushForStatus(CourseLibraryStatusKind kind) => kind switch
    {
        CourseLibraryStatusKind.Conflict => AppMaterialLayer.Brush(this, AppColorRole.StatusCritical, Colors.IndianRed),
        CourseLibraryStatusKind.Full => AppMaterialLayer.Brush(this, AppColorRole.StatusCritical, Colors.IndianRed),
        CourseLibraryStatusKind.Tight => AppMaterialLayer.Brush(this, AppColorRole.StatusCaution, Colors.Goldenrod),
        CourseLibraryStatusKind.CurrentPlan => AppMaterialLayer.Brush(this, AppColorRole.StatusCurrent, Colors.DeepSkyBlue),
        _ => AppBrushes.Transparent()
    };

    private static Symbol SymbolForStatus(CourseLibraryStatusKind kind) => kind switch
    {
        CourseLibraryStatusKind.Conflict => Symbol.Important,
        CourseLibraryStatusKind.Full => Symbol.Stop,
        CourseLibraryStatusKind.Tight => Symbol.Clock,
        CourseLibraryStatusKind.CurrentPlan => Symbol.Accept,
        _ => Symbol.Accept
    };
}
