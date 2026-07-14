namespace CoursePlanner.Services;

public enum PlannerPaneKind
{
    Library,
    Detail
}

/// <summary>
/// Owns the mutually-exclusive overlay state for the planner side panes.
/// Logical open state remains in the view model; this coordinator only tracks
/// which open overlay is in front and which one is temporarily suspended.
/// </summary>
public sealed class PlannerPaneStateCoordinator
{
    public PlannerPaneKind? ForegroundOverlay { get; private set; }

    public PlannerPaneKind? SuspendedOverlay { get; private set; }

    public bool IsSuppressed(PlannerPaneKind pane) => SuspendedOverlay == pane;

    public void Reconcile(bool overlaysCanOverlap, bool libraryOpen, bool detailOpen)
    {
        if (!overlaysCanOverlap || (!libraryOpen && !detailOpen))
        {
            Clear();
            return;
        }

        if (libraryOpen && detailOpen)
        {
            if (ForegroundOverlay is not { } foreground ||
                !IsOpen(foreground, libraryOpen, detailOpen))
            {
                foreground = PlannerPaneKind.Detail;
            }

            ForegroundOverlay = foreground;
            SuspendedOverlay = Other(foreground);
            return;
        }

        ForegroundOverlay = libraryOpen
            ? PlannerPaneKind.Library
            : PlannerPaneKind.Detail;
        SuspendedOverlay = null;
    }

    public PlannerPaneKind? PlanActivation(
        PlannerPaneKind target,
        bool overlaysCanOverlap,
        bool libraryOpen,
        bool detailOpen)
    {
        Reconcile(overlaysCanOverlap, libraryOpen, detailOpen);
        if (!overlaysCanOverlap)
            return null;

        var other = Other(target);
        return ForegroundOverlay == other && IsOpen(other, libraryOpen, detailOpen)
            ? other
            : null;
    }

    public void CommitActivation(PlannerPaneKind target, PlannerPaneKind suspended)
    {
        if (target == suspended)
            throw new ArgumentException("The foreground and suspended panes must differ.", nameof(suspended));

        ForegroundOverlay = target;
        SuspendedOverlay = suspended;
    }

    public PlannerPaneKind? PlanRestoreAfterClose(PlannerPaneKind closingPane) =>
        ForegroundOverlay == closingPane ? SuspendedOverlay : null;

    public void CommitClose(PlannerPaneKind closingPane)
    {
        if (ForegroundOverlay == closingPane)
        {
            ForegroundOverlay = SuspendedOverlay;
            SuspendedOverlay = null;
            return;
        }

        if (SuspendedOverlay == closingPane)
            SuspendedOverlay = null;
    }

    public void Clear()
    {
        ForegroundOverlay = null;
        SuspendedOverlay = null;
    }

    private static bool IsOpen(PlannerPaneKind pane, bool libraryOpen, bool detailOpen) =>
        pane == PlannerPaneKind.Library ? libraryOpen : detailOpen;

    private static PlannerPaneKind Other(PlannerPaneKind pane) =>
        pane == PlannerPaneKind.Library ? PlannerPaneKind.Detail : PlannerPaneKind.Library;
}
