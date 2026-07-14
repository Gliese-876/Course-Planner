using Microsoft.UI.Xaml;

namespace CoursePlanner.Services;

public sealed class WindowInteropService
{
    private readonly Func<Window?> _windowAccessor;

    public WindowInteropService(Func<Window?> windowAccessor)
    {
        _windowAccessor = windowAccessor;
    }

    public Window Window =>
        _windowAccessor() ?? throw new InvalidOperationException("Application window is not available.");

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(Window);
}
