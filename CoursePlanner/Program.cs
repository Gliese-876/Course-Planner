using System.Diagnostics;
using System.Runtime.InteropServices;
using CoursePlanner.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CoursePlanner;

internal static class Program
{
    private const string SingleInstanceKey = "CoursePlanner.PrimaryInstance";
    private static readonly RedirectedActivationState ActivationState = new();
    private static App? _app;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (!keyInstance.IsCurrent)
            return TryRedirectActivationTo(activationArgs, keyInstance) ? 0 : 1;

        keyInstance.Activated += OnActivated;
        Application.Start(_initialization =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App();
        });

        GC.KeepAlive(keyInstance);
        return 0;
    }

    internal static void NotifyMainWindowReady(App app)
    {
        Volatile.Write(ref _app, app);
        if (ActivationState.MarkWindowReady())
            app.ActivateMainWindowFromRedirect();
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (!ActivationState.RequestActivation())
            return;

        Volatile.Read(ref _app)?.ActivateMainWindowFromRedirect();
    }

    private static bool TryRedirectActivationTo(
        AppActivationArguments args,
        AppInstance keyInstance)
    {
        EventWaitHandle? redirectCompleted = null;
        Task? redirectTask = null;
        try
        {
            redirectCompleted = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset);
            var completionSignal = redirectCompleted;
            redirectTask = Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(args);
                }
                finally
                {
                    try
                    {
                        completionSignal.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The secondary process is already leaving after a
                        // native wait failure. No application state was opened.
                    }
                }
            });

            var handles = new[] { redirectCompleted.SafeWaitHandle.DangerousGetHandle() };
            var result = CoWaitForMultipleObjects(
                flags: 0,
                timeoutMilliseconds: SingleInstanceRedirectPolicy.TimeoutMilliseconds,
                handleCount: 1,
                handles,
                out _);
            if (SingleInstanceRedirectPolicy.IsTimeout(result) || result != 0)
            {
                DisposeAfterCompletion(redirectTask, redirectCompleted);
                redirectCompleted = null;
                return false;
            }

            redirectTask.GetAwaiter().GetResult();
            redirectCompleted.Dispose();
            redirectCompleted = null;

            TryForegroundPrimaryInstance(keyInstance);
            return true;
        }
        catch (Exception exception) when (SingleInstanceRedirectPolicy.IsOperationalFailure(exception))
        {
            Debug.WriteLine($"Single-instance activation redirection failed: {exception}");
            if (redirectTask is { IsCompleted: false } && redirectCompleted is not null)
            {
                DisposeAfterCompletion(redirectTask, redirectCompleted);
                redirectCompleted = null;
            }
            return false;
        }
        finally
        {
            redirectCompleted?.Dispose();
        }
    }

    private static void TryForegroundPrimaryInstance(AppInstance keyInstance)
    {
        try
        {
            using var primaryProcess = Process.GetProcessById((int)keyInstance.ProcessId);
            var mainWindowHandle = primaryProcess.MainWindowHandle;
            if (mainWindowHandle != IntPtr.Zero)
                _ = SetForegroundWindow(mainWindowHandle);
        }
        catch (Exception exception) when (SingleInstanceRedirectPolicy.IsForegroundFailure(exception))
        {
            // Redirection already succeeded, but the primary process exited
            // before the best-effort foreground request could be issued.
            Debug.WriteLine($"Primary instance exited after activation redirection: {exception}");
        }
    }

    private static void DisposeAfterCompletion(Task redirectTask, EventWaitHandle waitHandle) =>
        _ = redirectTask.ContinueWith(
            static (completedTask, state) =>
            {
                _ = completedTask.Exception;
                ((EventWaitHandle)state!).Dispose();
            },
            waitHandle,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(
        uint flags,
        uint timeoutMilliseconds,
        uint handleCount,
        IntPtr[] handles,
        out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
