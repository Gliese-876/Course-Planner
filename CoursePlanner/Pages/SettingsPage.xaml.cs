using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using System.Globalization;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

namespace CoursePlanner.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private ApplicationServices? _services;

    public SettingsPage()
    {
        InitializeComponent();
        AppTypography.Apply(this);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Unloaded += SettingsPage_Unloaded;
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    private DocumentSession Documents => _services!.Documents;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("SettingsPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Settings;
        DataContext = ViewModel;
        services.Documents.RolledBack += Documents_RolledBack;
        services.BackgroundOperations.Changed += BackgroundOperations_Changed;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        RefreshLocalizedControls();
    }

    private void BackgroundOperations_Changed(object? sender, EventArgs e) =>
        ApplyBackgroundOperationState();

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            _services.Documents.RolledBack -= Documents_RolledBack;
            _services.BackgroundOperations.Changed -= BackgroundOperations_Changed;
            _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        }
        Unloaded -= SettingsPage_Unloaded;
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        RefreshLocalizedControls();

    private void Documents_RolledBack(object? sender, EventArgs e) =>
        RefreshLocalizedControls();

    private void RefreshLocalizedControls()
    {
        _loading = true;
        InitializeControls();
        LoadSettingsCore();
        _loading = false;
    }

    private void ApplyBackgroundOperationState()
    {
        var busy = _services?.BackgroundOperations.IsBusy == true;
        BackupButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
    }

    private void InitializeControls()
    {
        var t = ViewModel.T;
        SettingsTitle.Text = t["Settings"];
        GeneralTitle.Text = t["General"];
        DataTitle.Text = t["DataManagement"];
        SoftwareInformationTitle.Text = t["SoftwareInformation"];
        LanguageTitleText.Text = t["Language"];
        LanguageDescriptionText.Text = t["LanguageDescription"];
        ThemeTitleText.Text = t["Theme"];
        ThemeDescriptionText.Text = t["ThemeDescription"];
        DatabaseTitleText.Text = t["DatabaseLocation"];
        DatabaseDescriptionText.Text = t["DatabaseLocationDescription"];
        BackupTitleText.Text = t["BackupAndExchange"];
        BackupDescriptionText.Text = t["BackupAndExchangeDescription"];
        AppNameText.Text = t["AppTitle"];
        AppDescriptionText.Text = t["AppDescription"];
        AppVersionText.Text = string.Format(
            CultureInfo.CurrentCulture,
            t["AppVersionFormat"],
            CurrentAppVersion());
        PublisherTitleText.Text = t["Publisher"];
        PublisherNameText.Text = Package.Current.PublisherDisplayName;
        ProjectLicenseTitleText.Text = t["OpenSourceLicense"];
        ProjectLicenseDescriptionText.Text = string.Format(
            CultureInfo.CurrentCulture,
            t["OpenSourceLicenseDescriptionFormat"],
            t["AppTitle"]);
        LicensesTitleText.Text = t["ThirdPartyLicenses"];
        LicensesDescriptionText.Text = t["LicensesDescription"];
        OpenDatabaseButton.Content = t["OpenDatabaseFolder"];
        BackupButton.Content = t["Backup"];
        RestoreButton.Content = t["Restore"];
        OpenPublisherProfileButton.Content = t["OpenGitHubProfile"];
        ViewProjectLicenseButton.Content = t["ViewMitLicense"];
        ViewLicensesButton.Content = t["ViewLicenses"];
        LanguageBox.ItemsSource = new[] { t["FollowSystem"], t["Chinese"], t["English"] };
        ThemeBox.ItemsSource = new[] { t["FollowSystem"], t["Light"], t["Dark"] };
        ApplyBackgroundOperationState();
    }

    private void LoadSettings()
    {
        _loading = true;
        LoadSettingsCore();
        _loading = false;
    }

    private void LoadSettingsCore()
    {
        LanguageBox.SelectedIndex = ViewModel.Language switch
        {
            LanguageMode.SimplifiedChinese => 1,
            LanguageMode.English => 2,
            _ => 0
        };
        ThemeBox.SelectedIndex = ViewModel.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0
        };
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
            return;
        ViewModel.Language = LanguageBox.SelectedIndex switch
        {
            1 => LanguageMode.SimplifiedChinese,
            2 => LanguageMode.English,
            _ => LanguageMode.FollowSystem
        };
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0)
            return;
        ViewModel.Theme = ThemeBox.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.FollowSystem
        };
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"course-planner-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip"
        };
        picker.FileTypeChoices.Add("ZIP", new List<string> { ".zip" });
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        try
        {
            var completed = await _services!.BackgroundOperations.RunAsync(
                ViewModel.T["Backup"],
                () => Task.Run(() => BackupService.CreateBackup(Documents.Repository.DatabasePath, file.Path)));
            if (!completed)
                return;
            await ShowMessageAsync(ViewModel.T["BackupCreated"], file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessageAsync(ViewModel.T["BackupFailed"], ex.Message);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(ViewModel.T["RestoreBackup"], ViewModel.T["RestoreConfirm"]))
            return;
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        try
        {
            string? preBackup = null;
            var completed = await _services!.BackgroundOperations.RunAsync(
                ViewModel.T["Restore"],
                async () =>
                {
                    var transaction = await Task.Run(() =>
                        Documents.BeginBackupRestore(
                            file.Path,
                            Path.Combine(Documents.Repository.DataDirectory, "automatic-backups")));
                    preBackup = Documents.ApplyBackupRestore(
                        transaction,
                        RefreshSettingsAfterRestore);
                });
            if (!completed)
                return;
            var completionMessage = string.IsNullOrWhiteSpace(preBackup)
                ? ViewModel.T["RestoreComplete"]
                : $"{ViewModel.T["PreRestoreBackup"]}: {preBackup}";
            await ShowMessageAsync(ViewModel.T["RestoreComplete"], completionMessage);
        }
        catch (DocumentRestorePostCommitException postCommitException) when (
            !RuntimeOperationExceptionPolicy.IsFatal(postCommitException))
        {
            await ResolveCommittedRestoreAsync(postCommitException);
        }
        catch (BackupRestoreCleanupException cleanupException) when (
            !RuntimeOperationExceptionPolicy.IsFatal(cleanupException))
        {
            await ShowMessageAsync(
                ViewModel.T["RestoreComplete"],
                string.Format(
                    CultureInfo.CurrentCulture,
                    ViewModel.T["RestoreCleanupWarningFormat"],
                    cleanupException.RecoveryDirectory));
        }
        catch (DocumentRestoreConsistencyException consistencyException) when (
            !RuntimeOperationExceptionPolicy.IsFatal(consistencyException))
        {
            await ResolveInterruptedRestoreAsync(consistencyException);
        }
        catch (Exception ex) when (IsRecoverableRestoreFailure(ex))
        {
            await ShowMessageAsync(ViewModel.T["RestoreFailed"], ex.Message);
        }
    }

    private async Task ResolveCommittedRestoreAsync(
        DocumentRestorePostCommitException postCommitException)
    {
        Exception currentFailure = postCommitException;
        while (true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["RestoreCommittedRefreshRequired"],
                Content = new TextBlock
                {
                    Text = $"{ViewModel.T["RestoreCommittedRefreshMessage"]}{Environment.NewLine}{Environment.NewLine}" +
                           currentFailure.Message,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ViewModel.T["Retry"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Primary
            };
            if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                PreserveInterruptedRestoreRecoveryAction();
                return;
            }

            try
            {
                Documents.ReloadFromRepository();
                RefreshSettingsAfterRestore();
                var completionMessage = ViewModel.T["RestoreCommittedRefreshRecoveryCompleteMessage"];
                if (postCommitException.CleanupException is BackupRestoreCleanupException cleanupException)
                {
                    completionMessage += Environment.NewLine + Environment.NewLine +
                        string.Format(
                            CultureInfo.CurrentCulture,
                            ViewModel.T["RestoreCleanupWarningFormat"],
                            cleanupException.RecoveryDirectory);
                }

                await ShowMessageAsync(
                    ViewModel.T["RestoreRecoveryComplete"],
                    completionMessage);
                return;
            }
            catch (Exception ex) when (IsRecoverableRestoreFailure(ex))
            {
                currentFailure = ex;
            }
        }
    }

    private async Task ResolveInterruptedRestoreAsync(
        DocumentRestoreConsistencyException consistencyException)
    {
        Exception currentFailure = consistencyException;
        while (true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["RestoreFailed"],
                Content = new TextBlock
                {
                    Text = currentFailure.Message,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ViewModel.T["Retry"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Primary
            };
            if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                PreserveInterruptedRestoreRecoveryAction();
                return;
            }

            try
            {
                Documents.ReloadFromRepository();
                RefreshSettingsAfterRestore();
                await ShowMessageAsync(
                    ViewModel.T["RestoreRecoveryComplete"],
                    ViewModel.T["RestoreRecoveryCompleteMessage"]);
                return;
            }
            catch (Exception ex) when (IsRecoverableRestoreFailure(ex))
            {
                currentFailure = ex;
            }
        }
    }

    private void PreserveInterruptedRestoreRecoveryAction()
    {
        if ((Documents.IsStorageConsistencyUnknown || Documents.IsSessionConsistencyUnknown) &&
            _services!.Windowing.Window is MainWindow mainWindow)
        {
            mainWindow.TryShowRuntimeOperationError();
        }
    }

    private static bool IsRecoverableRestoreFailure(Exception exception) =>
        !RuntimeOperationExceptionPolicy.IsFatal(exception) &&
        (RuntimeOperationExceptionPolicy.IsRecoverable(exception) ||
         exception is DocumentRestoreCompensationException or
                      DocumentSessionConsistencyException or
                      DocumentSessionRollbackException);

    private void RefreshSettingsAfterRestore()
    {
        _services!.Localization.RefreshLanguage();
        _services.Theme.RefreshTheme();
        LoadSettings();
    }

    private async void OpenDatabase_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(ViewModel.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            await Launcher.LaunchFolderPathAsync(directory);
    }

    private async void OpenPublisherProfile_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://github.com/Gliese-876"));

    private async void ViewLicenses_Click(object sender, RoutedEventArgs e)
    {
        var content = new TextBlock
        {
            Text = BuildLicenseNoticeText(),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["ThirdPartyLicenses"],
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = content
            },
            CloseButtonText = ViewModel.T["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private async void ViewProjectLicense_Click(object sender, RoutedEventArgs e)
    {
        var licenseFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///LICENSE"));
        var licenseText = await FileIO.ReadTextAsync(licenseFile);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["MitLicenseTitle"],
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock
                {
                    Text = licenseText,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = ViewModel.T["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private string BuildLicenseNoticeText()
    {
        var t = ViewModel.T;
        var separator = $"{Environment.NewLine}{Environment.NewLine}";
        return string.Join(
            separator,
            t["LicenseNoticeIntro"],
            "CommunityToolkit.Mvvm / CommunityToolkit.WinUI.Controls\r\nMIT License\r\nhttps://github.com/CommunityToolkit/Windows",
            "SkiaSharp\r\nMIT License\r\nhttps://github.com/mono/SkiaSharp",
            "Microsoft.Data.Sqlite\r\nMIT License\r\nhttps://github.com/dotnet/efcore",
            "SQLitePCLRaw\r\nApache License 2.0\r\nhttps://github.com/ericsink/SQLitePCL.raw",
            "Dream Han Sans SC\r\nSIL Open Font License 1.1\r\nhttps://github.com/Pal3love/dream-han-cjk");
    }

    private static string CurrentAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var responsiveWidth = TwoPaneLayoutService.ResolveWidth(this, width);
        TwoPaneLayoutService.SizeScrollableContent(SettingsScrollViewer, SettingsContentHost, SettingsContentRoot, responsiveWidth, 1040);

        SettingsTitle.Visibility = responsiveWidth < TwoPaneLayoutService.CompactBreakpoint
            ? Visibility.Collapsed
            : Visibility.Visible;

        var compact = responsiveWidth < 960;
        LanguageBox.Width = compact ? double.NaN : 240;
        ThemeBox.Width = compact ? double.NaN : 240;
        PlaceSettingContent(LanguageBox, compact);
        PlaceSettingContent(ThemeBox, compact);
        PlaceSettingContent(OpenDatabaseButton, compact);
        PlaceSettingContent(BackupActionsPanel, compact);
        PlaceSettingContent(AppVersionText, compact);
        PlaceSettingContent(OpenPublisherProfileButton, compact);
        PlaceSettingContent(ViewProjectLicenseButton, compact);
        PlaceSettingContent(ViewLicensesButton, compact);
        LanguageBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ThemeBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        OpenDatabaseButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        AppVersionText.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        OpenPublisherProfileButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ViewProjectLicenseButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ViewLicensesButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;

        BackupActionsPanel.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        BackupActionsPanel.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;

        foreach (var button in BackupActionsPanel.Children.OfType<Button>())
            button.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    private static void PlaceSettingContent(FrameworkElement element, bool compact)
    {
        Grid.SetColumn(element, compact ? 0 : 1);
        Grid.SetColumnSpan(element, compact ? 2 : 1);
        Grid.SetRow(element, compact ? 2 : 0);
        Grid.SetRowSpan(element, compact ? 1 : 2);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = ViewModel.T["OK"],
            CloseButtonText = ViewModel.T["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = ViewModel.T["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }
}
