using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class PlanTabCloseSequenceStateTests
{
    [Fact]
    public void SequenceUsesRightReserveThenLeadingInsetForLeftFill()
    {
        var state = new PlanTabCloseSequenceState();

        var right = state.BeginStep(sourceIndex: 6, tabCount: 8, tabWidth: 72, anchorX: 500);
        var left = state.BeginStep(sourceIndex: 6, tabCount: 7, tabWidth: 72, anchorX: 500);
        var correction = state.AlignReplacementToAnchor(replacementCloseX: 428);

        Assert.Equal(PlanTabCloseFillDirection.Right, right.Direction);
        Assert.True(right.Started);
        Assert.False(right.IsRightToLeftHandoff);
        Assert.Equal(72, state.TrailingReserve);
        Assert.Equal(PlanTabCloseFillDirection.Left, left.Direction);
        Assert.False(left.Started);
        Assert.True(left.IsRightToLeftHandoff);
        Assert.Equal(72, correction);
        Assert.Equal(72, state.LeadingInset);
        Assert.Equal(5, state.ExpectedSourceIndex(tabCount: 6));
    }

    [Fact]
    public void ResetClearsAllTemporaryCloseGeometry()
    {
        var state = new PlanTabCloseSequenceState();
        state.BeginStep(sourceIndex: 2, tabCount: 4, tabWidth: 120, anchorX: 420);
        state.AlignReplacementToAnchor(replacementCloseX: 360);

        state.Reset();

        Assert.False(state.IsActive);
        Assert.Equal(-1, state.ExpectedSourceIndex(tabCount: 3));
        Assert.Equal(0, state.LeadingInset);
        Assert.Equal(0, state.TrailingReserve);
    }

    [Fact]
    public void MaximumWidthReflowRequiresAllThreeConditions()
    {
        var allowed = ActiveState();
        var notRightmost = ActiveState();
        var fillsViewport = ActiveState();
        var crossesAnchor = ActiveState();

        var allowedMode = allowed.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 520,
            closeTrailingInset: 13);
        var notRightmostMode = notRightmost.UpdateLayoutAfterClose(
            sourceWasRightmost: false,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 520,
            closeTrailingInset: 13);
        var fillsViewportMode = fillsViewport.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 480,
            anchorDistanceFromLeft: 520,
            closeTrailingInset: 13);
        var crossesAnchorMode = crossesAnchor.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 479,
            closeTrailingInset: 13);

        Assert.Equal(PlanTabCloseLayoutMode.MaximumWidth, allowedMode);
        Assert.Equal(PlanTabCloseLayoutMode.Frozen, notRightmostMode);
        Assert.Equal(PlanTabCloseLayoutMode.FillLeft, fillsViewportMode);
        Assert.Equal(PlanTabCloseLayoutMode.FillLeft, crossesAnchorMode);
        Assert.Equal(240, allowed.TabWidth);
        Assert.Equal(0, allowed.TrailingReserve);
        Assert.Equal(0, allowed.LeadingInset);
        Assert.Equal(132, notRightmost.TabWidth);
        Assert.Equal(240, fillsViewport.TabWidth);
        Assert.Equal(240, crossesAnchor.TabWidth);
    }

    [Fact]
    public void OneRemainingTabSatisfiesTheAnchorAlternativeOnly()
    {
        var state = ActiveState();

        state.AlignReplacementToAnchor(replacementCloseX: 448);
        var mode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 1,
            singleTabWidth: 240,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 100,
            closeTrailingInset: 13);

        Assert.Equal(PlanTabCloseLayoutMode.MaximumWidth, mode);
        Assert.Equal(240, state.TabWidth);
        Assert.Equal(0, state.LeadingInset);
    }

    [Fact]
    public void ReflowConditionUsesTheActualSingleTabLayoutWidth()
    {
        var state = ActiveState();

        var mode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 200,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 420,
            closeTrailingInset: 13);

        Assert.Equal(PlanTabCloseLayoutMode.MaximumWidth, mode);
        Assert.Equal(200, state.TabWidth);
    }

    [Fact]
    public void LeftFillDynamicallyExpandsTabsAcrossTheFixedAnchorArea()
    {
        var state = ActiveState();

        var mode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 3,
            singleTabWidth: 240,
            tabViewportWidth: 480,
            anchorDistanceFromLeft: 520,
            closeTrailingInset: 13);

        Assert.Equal(PlanTabCloseLayoutMode.FillLeft, mode);
        Assert.Equal(533d / 3, state.TabWidth, precision: 6);
        Assert.Equal(0, state.LeadingInset);
        Assert.Equal(0, state.TrailingReserve);
    }

    [Fact]
    public void RightToLeftHandoffDefersWidthReflowUntilTheNextLeftClose()
    {
        var state = new PlanTabCloseSequenceState();
        state.BeginStep(sourceIndex: 3, tabCount: 5, tabWidth: 132, anchorX: 520);
        var handoff = state.BeginStep(sourceIndex: 3, tabCount: 4, tabWidth: 132, anchorX: 520);

        var handoffMode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 3,
            singleTabWidth: 240,
            tabViewportWidth: 676,
            anchorDistanceFromLeft: 520,
            closeTrailingInset: 13,
            deferReflowForHandoff: handoff.IsRightToLeftHandoff);
        var handoffWidth = state.TabWidth;
        var handoffTrailingReserve = state.TrailingReserve;
        var nextLeft = state.BeginStep(sourceIndex: 2, tabCount: 3, tabWidth: 132, anchorX: 520);
        var nextLeftMode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 480,
            anchorDistanceFromLeft: 400,
            closeTrailingInset: 13,
            deferReflowForHandoff: nextLeft.IsRightToLeftHandoff);

        Assert.True(handoff.IsRightToLeftHandoff);
        Assert.Equal(PlanTabCloseLayoutMode.Frozen, handoffMode);
        Assert.Equal(132, handoffWidth, precision: 6);
        Assert.Equal(132, handoffTrailingReserve, precision: 6);
        Assert.False(nextLeft.IsRightToLeftHandoff);
        Assert.Equal(PlanTabCloseLayoutMode.FillLeft, nextLeftMode);
        Assert.Equal(206.5, state.TabWidth, precision: 6);
    }

    [Fact]
    public void AnchorAlignmentCanReduceAnExistingLeadingInset()
    {
        var state = ActiveState();
        state.AlignReplacementToAnchor(replacementCloseX: 448);

        var adjustment = state.AlignReplacementToAnchor(replacementCloseX: 568);

        Assert.Equal(-48, adjustment);
        Assert.Equal(24, state.LeadingInset);
    }

    [Fact]
    public void LockedLeftFillUsesTheCurrentLeftStepCloseAnchor()
    {
        var state = ActiveState();
        var handoff = state.BeginStep(sourceIndex: 3, tabCount: 4, tabWidth: 132, anchorX: 514);
        var nextLeft = state.BeginStep(sourceIndex: 2, tabCount: 3, tabWidth: 132, anchorX: 512);

        var mode = state.UpdateLayoutAfterClose(
            sourceWasRightmost: true,
            remainingTabCount: 2,
            singleTabWidth: 240,
            tabViewportWidth: 480,
            anchorDistanceFromLeft: 512,
            closeTrailingInset: 13,
            deferReflowForHandoff: nextLeft.IsRightToLeftHandoff);
        var adjustment = state.AlignReplacementToAnchor(replacementCloseX: 470);

        Assert.True(handoff.IsRightToLeftHandoff);
        Assert.False(nextLeft.IsRightToLeftHandoff);
        Assert.Equal(512, state.AnchorX);
        Assert.Equal(PlanTabCloseLayoutMode.FillLeft, mode);
        Assert.Equal(42, adjustment);
        Assert.Equal(42, state.LeadingInset);
    }

    [Fact]
    public void AnchorAlignmentCanShiftEitherDirectionFromZeroInset()
    {
        var state = ActiveState();

        var adjustment = state.AlignReplacementToAnchor(replacementCloseX: 568);

        Assert.Equal(-48, adjustment);
        Assert.Equal(-48, state.LeadingInset);
    }

    private static PlanTabCloseSequenceState ActiveState()
    {
        var state = new PlanTabCloseSequenceState();
        state.BeginStep(sourceIndex: 3, tabCount: 5, tabWidth: 132, anchorX: 520);
        return state;
    }
}
