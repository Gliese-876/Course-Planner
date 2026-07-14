using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CoursePlanner.Services;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CoursePlanner;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : global::Microsoft.UI.Xaml.Application
{
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            AppTypography.InitializeResources();
            var services = new ApplicationServices(() => _window);
            _window = new MainWindow(services);
            _window.Closed += (_, _) => services.RegistrationOrders.Close();
            _window.Activate();
        }
        catch (Exception exception) when (RuntimeOperationExceptionPolicy.IsRecoverable(exception))
        {
            ShowStartupFailure(exception);
        }

        Program.NotifyMainWindowReady(this);
    }

    internal void ActivateMainWindowFromRedirect()
    {
        var window = _window;
        if (window is null)
            return;

        void ActivateWindow()
        {
            if (window is MainWindow mainWindow)
                mainWindow.ActivateFromRedirectedLaunch();
            else
                window.Activate();
        }

        if (window.DispatcherQueue.HasThreadAccess)
        {
            ActivateWindow();
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(ActivateWindow);
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        if (!RuntimeOperationExceptionPolicy.IsRecoverable(args.Exception))
            return;
        if (_window is not MainWindow mainWindow)
            return;
        if (!mainWindow.TryShowRuntimeOperationError())
            return;

        args.Handled = true;
    }

    private void ShowStartupFailure(Exception exception)
    {
        string dataDirectory;
        try
        {
            dataDirectory = ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception pathException) when (RuntimeOperationExceptionPolicy.IsRecoverable(pathException))
        {
            dataDirectory = $"(unavailable: {pathException.GetType().Name})";
        }

        var details = StartupFailureDetails.Create(exception, dataDirectory);
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = details.Title,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = details.Summary,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBox
        {
            Text = details.TechnicalDetails,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 180
        });
        panel.Children.Add(closeButton);

        _window = new Window
        {
            Title = details.Title,
            Content = new ScrollViewer
            {
                Content = new Border
                {
                    Padding = new Thickness(32),
                    Child = panel
                }
            }
        };
        closeButton.Click += (_, _) => _window.Close();
        _window.Activate();
    }
}
