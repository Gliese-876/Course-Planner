namespace CoursePlanner.Tests;

public sealed class SingleInstanceArchitectureTests
{
    [Fact]
    public void CustomMainRedirectsBeforeConstructingTheXamlApplication()
    {
        var project = Read("CoursePlanner", "CoursePlanner.csproj");
        var program = Read("CoursePlanner", "Program.cs");

        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", project, StringComparison.Ordinal);
        Assert.Contains("[STAThread]", program, StringComparison.Ordinal);
        Assert.Contains("WinRT.ComWrappersSupport.InitializeComWrappers();", program, StringComparison.Ordinal);
        Assert.Contains("AppInstance.GetCurrent().GetActivatedEventArgs()", program, StringComparison.Ordinal);
        Assert.Contains("AppInstance.FindOrRegisterForKey", program, StringComparison.Ordinal);
        Assert.Contains("if (!keyInstance.IsCurrent)", program, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", program, StringComparison.Ordinal);
        Assert.Contains("SingleInstanceRedirectPolicy.TimeoutMilliseconds", program, StringComparison.Ordinal);
        Assert.Contains("SingleInstanceRedirectPolicy.IsTimeout", program, StringComparison.Ordinal);
        Assert.Contains("TryRedirectActivationTo", program, StringComparison.Ordinal);
        Assert.Contains("? 0 : 1", program, StringComparison.Ordinal);
        Assert.Contains("Application.Start", program, StringComparison.Ordinal);
        Assert.Contains("new App()", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("if (!keyInstance.IsCurrent)", StringComparison.Ordinal) <
            program.IndexOf("new App()", StringComparison.Ordinal));
        Assert.DoesNotContain("new ApplicationServices", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinite", program, StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception exception) when (SingleInstanceRedirectPolicy.IsOperationalFailure(exception))",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception exception) when (SingleInstanceRedirectPolicy.IsForegroundFailure(exception))",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryInstanceReceivesRedirectedActivationAndForegroundsItsWindow()
    {
        var program = Read("CoursePlanner", "Program.cs");
        var app = Read("CoursePlanner", "App.xaml.cs");
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("keyInstance.Activated += OnActivated;", program, StringComparison.Ordinal);
        Assert.Contains("RedirectedActivationState", program, StringComparison.Ordinal);
        Assert.Contains("Program.NotifyMainWindowReady(this);", app, StringComparison.Ordinal);
        Assert.Contains("ActivateMainWindowFromRedirect", app, StringComparison.Ordinal);
        Assert.Contains("presenter.State == OverlappedPresenterState.Minimized", shell, StringComparison.Ordinal);
        Assert.Contains("presenter.Restore();", shell, StringComparison.Ordinal);
        Assert.Contains("Activate();", shell, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(RepositoryPaths.FromRoot(parts));
}
