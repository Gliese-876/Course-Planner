using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CoursePlanner.Pages;

public sealed partial class TimetableTransitionPage : Page
{
    private static readonly Dictionary<string, UIElement> QueuedContent = new();

    public TimetableTransitionPage()
    {
        InitializeComponent();
    }

    internal static string QueueContent(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var token = Guid.NewGuid().ToString("N");
        QueuedContent.Add(token, content);
        return token;
    }

    internal static void CancelQueuedContent(string token) =>
        QueuedContent.Remove(token);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not string token ||
            !QueuedContent.Remove(token, out var content))
        {
            throw new InvalidOperationException("TimetableTransitionPage requires queued content.");
        }

        Content = content;
    }
}
