using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class PlannerPaneStateCoordinatorTests
{
    [Fact]
    public void NarrowLayoutDefaultsToDetailAndSuspendsLibraryWhenBothAreOpen()
    {
        var state = new PlannerPaneStateCoordinator();

        state.Reconcile(overlaysCanOverlap: true, libraryOpen: true, detailOpen: true);

        Assert.Equal(PlannerPaneKind.Detail, state.ForegroundOverlay);
        Assert.Equal(PlannerPaneKind.Library, state.SuspendedOverlay);
        Assert.True(state.IsSuppressed(PlannerPaneKind.Library));
        Assert.False(state.IsSuppressed(PlannerPaneKind.Detail));
    }

    [Fact]
    public void ActivatingSuspendedLibrarySuspendsDetailAndClosingLibraryRestoresIt()
    {
        var state = new PlannerPaneStateCoordinator();
        state.Reconcile(overlaysCanOverlap: true, libraryOpen: true, detailOpen: true);

        var paneToSuspend = state.PlanActivation(
            PlannerPaneKind.Library,
            overlaysCanOverlap: true,
            libraryOpen: true,
            detailOpen: true);
        Assert.Equal(PlannerPaneKind.Detail, paneToSuspend);

        Assert.True(paneToSuspend.HasValue);
        state.CommitActivation(PlannerPaneKind.Library, paneToSuspend.GetValueOrDefault());
        Assert.Equal(PlannerPaneKind.Library, state.ForegroundOverlay);
        Assert.True(state.IsSuppressed(PlannerPaneKind.Detail));

        Assert.Equal(
            PlannerPaneKind.Detail,
            state.PlanRestoreAfterClose(PlannerPaneKind.Library));
        state.CommitClose(PlannerPaneKind.Library);

        Assert.Equal(PlannerPaneKind.Detail, state.ForegroundOverlay);
        Assert.Null(state.SuspendedOverlay);
    }

    [Fact]
    public void SwitchingBackAndForthKeepsExactlyOneForegroundOverlay()
    {
        var state = new PlannerPaneStateCoordinator();
        state.Reconcile(overlaysCanOverlap: true, libraryOpen: true, detailOpen: true);

        state.CommitActivation(PlannerPaneKind.Library, PlannerPaneKind.Detail);
        state.CommitActivation(PlannerPaneKind.Detail, PlannerPaneKind.Library);

        Assert.Equal(PlannerPaneKind.Detail, state.ForegroundOverlay);
        Assert.Equal(PlannerPaneKind.Library, state.SuspendedOverlay);
        Assert.NotEqual(state.ForegroundOverlay, state.SuspendedOverlay);
    }

    [Fact]
    public void NonOverlappingLayoutClearsTemporaryPresentationState()
    {
        var state = new PlannerPaneStateCoordinator();
        state.CommitActivation(PlannerPaneKind.Library, PlannerPaneKind.Detail);

        state.Reconcile(overlaysCanOverlap: false, libraryOpen: true, detailOpen: true);

        Assert.Null(state.ForegroundOverlay);
        Assert.Null(state.SuspendedOverlay);
    }

    [Fact]
    public void SingleOpenPaneCannotRemainSuspended()
    {
        var state = new PlannerPaneStateCoordinator();
        state.CommitActivation(PlannerPaneKind.Library, PlannerPaneKind.Detail);

        state.Reconcile(overlaysCanOverlap: true, libraryOpen: true, detailOpen: false);

        Assert.Equal(PlannerPaneKind.Library, state.ForegroundOverlay);
        Assert.Null(state.SuspendedOverlay);
    }
}
