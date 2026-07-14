namespace CoursePlanner.Services;

public sealed class ShellNavigationService
{
    public event EventHandler? SemestersRequested;
    public event EventHandler? PlannerRequested;

    public void RequestSemesters() =>
        SemestersRequested?.Invoke(this, EventArgs.Empty);

    public void RequestPlanner() =>
        PlannerRequested?.Invoke(this, EventArgs.Empty);
}
