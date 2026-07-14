using CoursePlanner.Persistence;
using CoursePlanner.ViewModels;
using Microsoft.UI.Xaml;
using System.Globalization;
using Windows.Storage;
using Windows.System.UserProfile;

namespace CoursePlanner.Services;

public sealed class ApplicationServices
{
    public ApplicationServices(Func<Window?> windowAccessor)
    {
        // CurrentUICulture can represent the app's runtime language. The user
        // profile list is the unmodified source of truth for FollowSystem.
        var systemUiCulture = SystemLanguagePreference.Resolve(
            GlobalizationPreferences.Languages,
            CultureInfo.CurrentUICulture);
        var dataPath = ApplicationData.Current.LocalFolder.Path;
        var installText = new AppLocalizer(CoursePlanner.Core.LanguageMode.FollowSystem, systemUiCulture);
        Windowing = new WindowInteropService(windowAccessor);
        Navigation = new ShellNavigationService();
        BackgroundOperations = new BackgroundOperationService();
        Documents = new DocumentSession(new SqliteAppRepository(
            dataPath,
            () => CoursePlanner.Core.SeedData.Create(
                installText["DefaultSemesterName"],
                installText["DefaultPlanName"])));
        Localization = new LocalizationService(Documents, systemUiCulture);
        Theme = new ThemeService(Documents, windowAccessor);
        Planner = new PlannerViewModel(Documents, Localization);
        ImportExport = new ImportExportCoordinator(Documents, Planner, Localization, Theme, BackgroundOperations);
        RegistrationOrderWindowPlacement = new ToolWindowPlacementState();
        RegistrationOrders = new RegistrationOrderWindowService(
            Documents,
            Localization,
            Theme,
            Planner,
            Windowing,
            RegistrationOrderWindowPlacement,
            BackgroundOperations);
        Settings = new SettingsViewModel(Documents, Localization, Theme);
    }

    public WindowInteropService Windowing { get; }
    public ShellNavigationService Navigation { get; }
    public BackgroundOperationService BackgroundOperations { get; }
    public DocumentSession Documents { get; }
    public LocalizationService Localization { get; }
    public ThemeService Theme { get; }
    public PlannerViewModel Planner { get; }
    public ImportExportCoordinator ImportExport { get; }
    public ToolWindowPlacementState RegistrationOrderWindowPlacement { get; }
    public RegistrationOrderWindowService RegistrationOrders { get; }
    public SettingsViewModel Settings { get; }
}
