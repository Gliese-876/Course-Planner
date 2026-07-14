using System.Text.RegularExpressions;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class CourseEditLeaveGuardTests
{
    [Fact]
    public async Task CancelLeavesEditSaveAndDiscardUntouched()
    {
        var choices = 0;
        var saves = 0;
        var discards = 0;

        var mayLeave = await CourseEditLeaveGuard.TryLeaveAsync(
            hasActiveEdit: true,
            hasUnsavedChanges: true,
            chooseAsync: () =>
            {
                choices++;
                return Task.FromResult(CourseEditLeaveChoice.Cancel);
            },
            saveAsync: () =>
            {
                saves++;
                return Task.FromResult(true);
            },
            discard: () => discards++);

        Assert.False(mayLeave);
        Assert.Equal(1, choices);
        Assert.Equal(0, saves);
        Assert.Equal(0, discards);
    }

    [Fact]
    public async Task FailedSavePreventsThePendingOperation()
    {
        var discards = 0;

        var mayLeave = await CourseEditLeaveGuard.TryLeaveAsync(
            hasActiveEdit: true,
            hasUnsavedChanges: true,
            chooseAsync: () => Task.FromResult(CourseEditLeaveChoice.Save),
            saveAsync: () => Task.FromResult(false),
            discard: () => discards++);

        Assert.False(mayLeave);
        Assert.Equal(0, discards);
    }

    [Theory]
    [InlineData(CourseEditLeaveChoice.Save)]
    [InlineData(CourseEditLeaveChoice.Discard)]
    public async Task SuccessfulResolutionAllowsThePendingOperation(CourseEditLeaveChoice choice)
    {
        var saves = 0;
        var discards = 0;

        var mayLeave = await CourseEditLeaveGuard.TryLeaveAsync(
            hasActiveEdit: true,
            hasUnsavedChanges: true,
            chooseAsync: () => Task.FromResult(choice),
            saveAsync: () =>
            {
                saves++;
                return Task.FromResult(true);
            },
            discard: () => discards++);

        Assert.True(mayLeave);
        Assert.Equal(choice == CourseEditLeaveChoice.Save ? 1 : 0, saves);
        Assert.Equal(choice == CourseEditLeaveChoice.Discard ? 1 : 0, discards);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task NoDirtyActiveEditSkipsTheDialog(bool hasActiveEdit, bool hasUnsavedChanges)
    {
        var choices = 0;

        var mayLeave = await CourseEditLeaveGuard.TryLeaveAsync(
            hasActiveEdit,
            hasUnsavedChanges,
            chooseAsync: () =>
            {
                choices++;
                return Task.FromResult(CourseEditLeaveChoice.Cancel);
            },
            saveAsync: () => Task.FromResult(false),
            discard: () => throw new InvalidOperationException("Discard must not run."));

        Assert.True(mayLeave);
        Assert.Equal(0, choices);
    }

    [Fact]
    public void SessionStateTracksTheBaselineIndependentlyOfPanelVisibility()
    {
        var session = new CourseEditSessionState();

        Assert.False(session.IsActive);
        session.Begin("baseline");
        Assert.True(session.IsActive);
        Assert.False(session.HasUnsavedChanges("baseline"));
        Assert.True(session.HasUnsavedChanges("changed"));
        session.End();
        Assert.False(session.IsActive);
        Assert.False(session.HasUnsavedChanges("changed"));
    }

    [Fact]
    public void PlannerUndoRedoAndLibraryEditReplacementUseTheSharedGuard()
    {
        var root = FindRepositoryRoot();
        var planner = File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var library = File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "CourseLibraryPage.xaml.cs"));

        Assert.Contains("public Task<bool> ConfirmLeavingCourseEditAsync()", planner);
        Assert.Contains("CourseEditLeaveGuard.TryLeaveAsync", planner);
        Assert.Contains("ExecuteHistoryCommandAsync", planner);
        Assert.Single(Regex.Matches(planner, @"Documents\.Undo\(\)").Cast<Match>());
        Assert.Single(Regex.Matches(planner, @"Documents\.Redo\(\)").Cast<Match>());
        Assert.DoesNotContain("private void Undo_Click", planner);
        Assert.DoesNotContain("private void Redo_Click", planner);

        Assert.Contains("public Task<bool> ConfirmLeavingCourseEditAsync()", library);
        Assert.Contains("CourseEditLeaveGuard.TryLeaveAsync", library);
        Assert.Contains("BeginLibraryEditAsync", library);
        Assert.DoesNotContain("private void BeginLibraryEdit(", library);
    }

    private static string FindRepositoryRoot() => RepositoryPaths.Root;
}
