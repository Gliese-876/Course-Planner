namespace CoursePlanner.Tests;

public sealed class BackupRestoreUiArchitectureTests
{
    [Fact]
    public void SettingsRestoreKeepsDatabaseReplacementSessionReloadAndRefreshInOneTransaction()
    {
        var settingsPage = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Pages",
            "SettingsPage.xaml.cs"));

        Assert.Contains("Documents.BeginBackupRestore(", settingsPage, StringComparison.Ordinal);
        Assert.Contains("Documents.ApplyBackupRestore(", settingsPage, StringComparison.Ordinal);
        Assert.Contains("RefreshSettingsAfterRestore", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupService.RestoreWithPreBackup(", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupService.BeginRestoreWithPreBackup(", settingsPage, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRestoreExposesRepeatableConsistencyRecovery()
    {
        var settingsPage = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Pages",
            "SettingsPage.xaml.cs"));
        var englishResources = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Application",
            "Resources",
            "en-US",
            "Resources.resw"));
        var chineseResources = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Application",
            "Resources",
            "zh-Hans",
            "Resources.resw"));

        Assert.Contains("catch (DocumentRestoreConsistencyException", settingsPage, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (IsRecoverableRestoreFailure(ex))", settingsPage, StringComparison.Ordinal);
        Assert.Contains("RuntimeOperationExceptionPolicy.IsRecoverable(exception)", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception ex) when (!RuntimeOperationExceptionPolicy.IsFatal(ex))", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ResolveInterruptedRestoreAsync", settingsPage, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = ViewModel.T[\"Retry\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("CloseButtonText = ViewModel.T[\"Cancel\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("Documents.ReloadFromRepository();", settingsPage, StringComparison.Ordinal);
        Assert.Contains("RefreshSettingsAfterRestore();", settingsPage, StringComparison.Ordinal);
        Assert.Contains("PreserveInterruptedRestoreRecoveryAction();", settingsPage, StringComparison.Ordinal);
        Assert.Contains("_services!.Windowing.Window is MainWindow mainWindow", settingsPage, StringComparison.Ordinal);
        Assert.Contains("mainWindow.TryShowRuntimeOperationError();", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.T[\"RestoreRecoveryCompleteMessage\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("<data name=\"Retry\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreRecoveryComplete\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreRecoveryCompleteMessage\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"Retry\"", chineseResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreRecoveryComplete\"", chineseResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreRecoveryCompleteMessage\"", chineseResources, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRestoreDistinguishesCommittedRefreshAndCleanupFailuresFromRollbackFailures()
    {
        var settingsPage = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Pages",
            "SettingsPage.xaml.cs"));
        var englishResources = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Application",
            "Resources",
            "en-US",
            "Resources.resw"));
        var chineseResources = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Application",
            "Resources",
            "zh-Hans",
            "Resources.resw"));

        var postCommitCatch = settingsPage.IndexOf(
            "catch (DocumentRestorePostCommitException",
            StringComparison.Ordinal);
        var consistencyCatch = settingsPage.IndexOf(
            "catch (DocumentRestoreConsistencyException",
            StringComparison.Ordinal);
        Assert.True(postCommitCatch >= 0);
        Assert.True(consistencyCatch > postCommitCatch);
        Assert.Contains("catch (BackupRestoreCleanupException", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ResolveCommittedRestoreAsync", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.T[\"RestoreCommittedRefreshRequired\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.T[\"RestoreCommittedRefreshRecoveryCompleteMessage\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.T[\"RestoreCleanupWarningFormat\"]", settingsPage, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCommittedRefreshRequired\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCommittedRefreshRecoveryCompleteMessage\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCleanupWarningFormat\"", englishResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCommittedRefreshRequired\"", chineseResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCommittedRefreshRecoveryCompleteMessage\"", chineseResources, StringComparison.Ordinal);
        Assert.Contains("<data name=\"RestoreCleanupWarningFormat\"", chineseResources, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] segments) =>
        RepositoryPaths.FromRoot(segments);
}
