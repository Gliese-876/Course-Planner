namespace CoursePlanner.Services;

public enum PlanTabCloseFillDirection
{
    Right,
    Left
}

public enum PlanTabCloseLayoutMode
{
    Frozen,
    FillLeft,
    MaximumWidth
}

public readonly record struct PlanTabCloseStep(
    PlanTabCloseFillDirection Direction,
    bool Started,
    bool IsRightToLeftHandoff);

/// <summary>
/// Tracks the temporary browser-style geometry used while repeatedly closing
/// tabs at one pointer location. Right-side tabs consume a trailing reserve;
/// once the anchor reaches the right edge, left-side tabs move into the same
/// close slot through a leading inset.
/// </summary>
public sealed class PlanTabCloseSequenceState
{
    private PlanTabCloseFillDirection? _previousDirection;

    public bool IsActive { get; private set; }
    public int AnchorIndex { get; private set; } = -1;
    public double TabWidth { get; private set; }
    public double AnchorX { get; private set; }
    public double LeadingInset { get; private set; }
    public double TrailingReserve { get; private set; }

    public int ExpectedSourceIndex(int tabCount) =>
        IsActive && tabCount > 0
            ? Math.Min(AnchorIndex, tabCount - 1)
            : -1;

    public PlanTabCloseStep BeginStep(
        int sourceIndex,
        int tabCount,
        double tabWidth,
        double anchorX)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tabCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceIndex, tabCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tabWidth, 0);
        if (!double.IsFinite(anchorX))
            throw new ArgumentOutOfRangeException(nameof(anchorX));

        var started = !IsActive;
        if (started)
        {
            IsActive = true;
            AnchorIndex = sourceIndex;
            TabWidth = tabWidth;
            AnchorX = anchorX;
            LeadingInset = 0;
            TrailingReserve = 0;
            _previousDirection = null;
        }

        var direction = sourceIndex < tabCount - 1
            ? PlanTabCloseFillDirection.Right
            : PlanTabCloseFillDirection.Left;
        if (direction == PlanTabCloseFillDirection.Left)
            AnchorX = anchorX;
        var isRightToLeftHandoff =
            _previousDirection == PlanTabCloseFillDirection.Right &&
            direction == PlanTabCloseFillDirection.Left;
        if (direction == PlanTabCloseFillDirection.Right)
            TrailingReserve += TabWidth;
        _previousDirection = direction;

        return new PlanTabCloseStep(direction, started, isRightToLeftHandoff);
    }

    /// <summary>
    /// Chooses the layout after a close. Right-side replacement keeps the
    /// frozen strip. The single right-to-left handoff frame also stays frozen
    /// so the selected tab does not jump when the replacement direction flips.
    /// Subsequent left-side replacement dynamically fills the fixed-anchor area
    /// until all three release conditions permit maximum-width tabs and release
    /// the temporary close anchor. During locked left fill, the caller aligns
    /// the replacement close button to the current step anchor.
    /// The maximum width is the width produced by the normal layout when only
    /// one tab exists, rather than an independently configured style constant.
    /// A single remaining tab is only the anchor-fit alternative; the tab must
    /// still be rightmost and leave unused viewport space.
    /// </summary>
    public PlanTabCloseLayoutMode UpdateLayoutAfterClose(
        bool sourceWasRightmost,
        int remainingTabCount,
        double singleTabWidth,
        double tabViewportWidth,
        double anchorDistanceFromLeft,
        double closeTrailingInset,
        bool deferReflowForHandoff = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(remainingTabCount, 0);
        ThrowIfNotPositiveFinite(singleTabWidth, nameof(singleTabWidth));
        ThrowIfNotPositiveFinite(tabViewportWidth, nameof(tabViewportWidth));
        if (!double.IsFinite(anchorDistanceFromLeft) || anchorDistanceFromLeft < 0)
            throw new ArgumentOutOfRangeException(nameof(anchorDistanceFromLeft));
        if (!double.IsFinite(closeTrailingInset) || closeTrailingInset < 0)
            throw new ArgumentOutOfRangeException(nameof(closeTrailingInset));

        if (!IsActive || !sourceWasRightmost || deferReflowForHandoff)
            return PlanTabCloseLayoutMode.Frozen;

        var singleTabWidthTotal = remainingTabCount * singleTabWidth;
        var leavesUnusedViewportSpace = singleTabWidthTotal < tabViewportWidth;
        var fitsBeforeAnchor =
            remainingTabCount == 1 || singleTabWidthTotal <= anchorDistanceFromLeft;
        TrailingReserve = 0;
        LeadingInset = 0;
        if (leavesUnusedViewportSpace && fitsBeforeAnchor)
        {
            TabWidth = singleTabWidth;
            return PlanTabCloseLayoutMode.MaximumWidth;
        }

        var fillWidth = (anchorDistanceFromLeft + closeTrailingInset) / remainingTabCount;
        TabWidth = Math.Min(singleTabWidth, Math.Max(TabWidth, fillWidth));
        return PlanTabCloseLayoutMode.FillLeft;
    }

    public double AlignReplacementToAnchor(double replacementCloseX)
    {
        if (!IsActive)
            return 0;
        if (!double.IsFinite(replacementCloseX))
            throw new ArgumentOutOfRangeException(nameof(replacementCloseX));

        var nextInset = LeadingInset + AnchorX - replacementCloseX;
        var adjustment = nextInset - LeadingInset;
        LeadingInset = nextInset;
        return adjustment;
    }

    public void Reset()
    {
        IsActive = false;
        AnchorIndex = -1;
        TabWidth = 0;
        AnchorX = 0;
        LeadingInset = 0;
        TrailingReserve = 0;
        _previousDirection = null;
    }

    private static void ThrowIfNotPositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
