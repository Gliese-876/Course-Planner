namespace CoursePlanner.Tests;

public sealed class RuntimeOperationErrorArchitectureTests
{
    [Fact]
    public void OnlyRecoverableStartupFailuresEnterTheFallbackWindow()
    {
        var app = Read("CoursePlanner", "App.xaml.cs");
        var launched = Slice(
            app,
            "protected override void OnLaunched",
            "internal void ActivateMainWindowFromRedirect");
        var startupFailure = Slice(
            app,
            "private void ShowStartupFailure",
            "\n}");

        Assert.Contains(
            "catch (Exception exception) when (RuntimeOperationExceptionPolicy.IsRecoverable(exception))",
            launched,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception pathException) when (RuntimeOperationExceptionPolicy.IsRecoverable(pathException))",
            startupFailure,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))",
            launched,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppHandlesOnlyPolicyApprovedExceptionsAfterTheShellPresentsThem()
    {
        var app = Read("CoursePlanner", "App.xaml.cs");
        var handler = Slice(
            app,
            "private void App_UnhandledException",
            "private void ShowStartupFailure");

        Assert.Contains("UnhandledException += App_UnhandledException;", app, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationExceptionPolicy.IsRecoverable(args.Exception)", handler, StringComparison.Ordinal);
        Assert.Contains("_window is not MainWindow mainWindow", handler, StringComparison.Ordinal);
        Assert.Contains("mainWindow.TryShowRuntimeOperationError()", handler, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true;", handler, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("mainWindow.TryShowRuntimeOperationError()", StringComparison.Ordinal) <
            handler.IndexOf("args.Handled = true;", StringComparison.Ordinal));
        Assert.Equal(
            1,
            handler.Split("args.Handled = true;", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void MainWindowUsesAnAccessibleNonModalErrorSurface()
    {
        var xaml = Read("CoursePlanner", "MainWindow.xaml");
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"RuntimeOperationErrorBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RuntimeOperationErrorBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<InfoBar.ActionButton>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RuntimeOperationRecoveryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RuntimeOperationRecoveryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RuntimeOperationRecoveryButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Severity=\"Error\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public bool TryShowRuntimeOperationError()", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationErrorTitle", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationErrorMessage", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationErrorBar.IsOpen = true;", shell, StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception presentationException) when (!RuntimeOperationExceptionPolicy.IsFatal(presentationException))",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeErrorActionRetriesOnlyAnUnknownDocumentSessionOncePerClick()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var handler = Slice(
            shell,
            "private void RuntimeOperationRecoveryButton_Click",
            "private bool RequiresRuntimeOperationRecovery");

        Assert.Contains("if (!RequiresRuntimeOperationRecovery)", handler, StringComparison.Ordinal);
        Assert.Contains("_services.Documents.ReloadFromRepository();", handler, StringComparison.Ordinal);
        Assert.Equal(
            1,
            handler.Split("_services.Documents.ReloadFromRepository();", StringSplitOptions.None).Length - 1);
        Assert.Contains("catch (Exception exception) when (IsRecoverableDocumentRecoveryFailure(exception))", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))", handler, StringComparison.Ordinal);
        Assert.Contains("_services.Localization.RefreshLanguage();", handler, StringComparison.Ordinal);
        Assert.Contains("_services.Theme.RefreshTheme();", handler, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationErrorBar.IsOpen = false;", handler, StringComparison.Ordinal);

        Assert.Contains("_services.Documents.IsStorageConsistencyUnknown", shell, StringComparison.Ordinal);
        Assert.Contains("_services.Documents.IsSessionConsistencyUnknown", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationExceptionPolicy.IsRecoverable(exception)", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationRecoveryButton.Visibility = requiresRecovery", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationRecoveryButton.IsEnabled = requiresRecovery;", shell, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationErrorBar.IsClosable = !requiresRecovery;", shell, StringComparison.Ordinal);
        Assert.Contains("_services.Localization.Localizer[\"Retry\"]", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeErrorTextIsLocalizedInEveryCatalog()
    {
        foreach (var language in new[] { "en-US", "zh-Hans" })
        {
            var resources = Read("CoursePlanner.Application", "Resources", language, "Resources.resw");
            Assert.Contains("<data name=\"RuntimeOperationErrorTitle\"", resources, StringComparison.Ordinal);
            Assert.Contains("<data name=\"RuntimeOperationErrorMessage\"", resources, StringComparison.Ordinal);
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        Assert.True(end > start, $"Missing marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(RepositoryPaths.FromRoot(parts));
}
