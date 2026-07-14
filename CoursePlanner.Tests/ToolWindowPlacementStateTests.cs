using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class ToolWindowPlacementStateTests
{
    [Fact]
    public void RemembersLatestBoundsOnlyWithinTheOwningSession()
    {
        var session = new ToolWindowPlacementState();

        Assert.False(session.TryGet(out _));

        session.Remember(new ToolWindowBounds(120, 80, 560, 600));
        session.Remember(new ToolWindowBounds(-900, 140, 720, 780));

        Assert.True(session.TryGet(out var remembered));
        Assert.Equal(new ToolWindowBounds(-900, 140, 720, 780), remembered);

        var restartedApplicationSession = new ToolWindowPlacementState();
        Assert.False(restartedApplicationSession.TryGet(out _));
    }

    [Fact]
    public void FitsRememberedBoundsInsideTheNearestWorkArea()
    {
        var remembered = new ToolWindowBounds(2400, -200, 1500, 1200);
        var workArea = new ToolWindowWorkArea(100, 50, 1200, 800);

        var fitted = ToolWindowPlacementState.FitWithinWorkArea(remembered, workArea);

        Assert.Equal(new ToolWindowBounds(100, 50, 1200, 800), fitted);
    }

    [Fact]
    public void KeepsValidNegativeMonitorCoordinatesUnchanged()
    {
        var remembered = new ToolWindowBounds(-1700, 120, 600, 640);
        var workArea = new ToolWindowWorkArea(-1920, 0, 1920, 1080);

        var fitted = ToolWindowPlacementState.FitWithinWorkArea(remembered, workArea);

        Assert.Equal(remembered, fitted);
    }

    [Fact]
    public void RejectsNonPositiveWindowSizes()
    {
        var session = new ToolWindowPlacementState();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Remember(new ToolWindowBounds(0, 0, 0, 300)));
    }
}
