namespace CoursePlanner.Services;

public readonly record struct ToolWindowBounds(
    int X,
    int Y,
    int Width,
    int Height);

public readonly record struct ToolWindowWorkArea(
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>
/// Holds secondary-window geometry for one application-services lifetime.
/// A new application process creates a new instance, so placement is never
/// persisted across app restarts.
/// </summary>
public sealed class ToolWindowPlacementState
{
    private ToolWindowBounds? _remembered;

    public bool TryGet(out ToolWindowBounds bounds)
    {
        if (_remembered is not { } value)
        {
            bounds = default;
            return false;
        }

        bounds = value;
        return true;
    }

    public void Remember(ToolWindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Window size must be positive.");

        _remembered = bounds;
    }

    public static ToolWindowBounds FitWithinWorkArea(
        ToolWindowBounds remembered,
        ToolWindowWorkArea workArea)
    {
        if (remembered.Width <= 0 || remembered.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(remembered), "Window size must be positive.");
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea), "Work area size must be positive.");

        var width = Math.Min(remembered.Width, workArea.Width);
        var height = Math.Min(remembered.Height, workArea.Height);
        var maxX = workArea.X + workArea.Width - width;
        var maxY = workArea.Y + workArea.Height - height;

        return new ToolWindowBounds(
            Math.Clamp(remembered.X, workArea.X, maxX),
            Math.Clamp(remembered.Y, workArea.Y, maxY),
            width,
            height);
    }
}
