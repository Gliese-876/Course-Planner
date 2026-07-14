using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Services;

/// <summary>
/// Restores the native PointerOver visual state for disabled buttons without
/// changing their disabled input or UI Automation semantics.
/// </summary>
public static class DisabledButtonHoverLayer
{
    private static readonly ConditionalWeakTable<FrameworkElement, RootTracker> Trackers = new();

    public static void Attach(Window window, FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);

        if (Trackers.TryGetValue(root, out _))
            return;

        Trackers.Add(root, new RootTracker(window, root));
    }

    public static void Detach(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Trackers.TryGetValue(root, out var tracker))
            return;

        tracker.Dispose();
        Trackers.Remove(root);
    }

    private sealed class RootTracker : IDisposable
    {
        private readonly FrameworkElement _root;
        private readonly DispatcherQueueTimer _pointerStateTimer;
        private readonly nint _windowHandle;
        private readonly List<ButtonBase> _buttonCache = new();
        private ButtonBase? _hoveredButton;
        private HoverVisual? _hoverVisual;
        private bool _isDisposed;
        private bool _buttonCacheDirty = true;
        private long _visualStateVersion;
        private long _lastButtonCacheRefresh;

        public RootTracker(Window window, FrameworkElement root)
        {
            _root = root;
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _pointerStateTimer = _root.DispatcherQueue.CreateTimer();
            _pointerStateTimer.Interval = TimeSpan.FromMilliseconds(50);
            _pointerStateTimer.IsRepeating = true;
            _pointerStateTimer.Tick += PointerStateTimer_Tick;
            _root.Loaded += Root_Loaded;
            _root.LayoutUpdated += Root_LayoutUpdated;
            _root.Unloaded += Root_Unloaded;

            if (_root.IsLoaded)
                _pointerStateTimer.Start();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            if (_hoveredButton is { } previous)
            {
                previous.IsEnabledChanged -= HoveredButton_IsEnabledChanged;
                _hoverVisual?.Restore();
                _hoveredButton = null;
                _hoverVisual = null;
            }

            _isDisposed = true;
            _visualStateVersion++;
            _root.Loaded -= Root_Loaded;
            _root.LayoutUpdated -= Root_LayoutUpdated;
            _root.Unloaded -= Root_Unloaded;
            _pointerStateTimer.Stop();
            _pointerStateTimer.Tick -= PointerStateTimer_Tick;
            _buttonCache.Clear();
        }

        private void Root_Loaded(object sender, RoutedEventArgs args) =>
            _pointerStateTimer.Start();

        private void Root_LayoutUpdated(object? sender, object args) =>
            _buttonCacheDirty = true;

        private void Root_Unloaded(object sender, RoutedEventArgs args) =>
            Dispose();

        private void PointerStateTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_windowHandle == 0 ||
                !GetCursorPos(out var cursor) ||
                !ScreenToClient(_windowHandle, ref cursor))
            {
                SetHoveredButton(null);
                return;
            }

            var scale = _root.XamlRoot?.RasterizationScale ?? 1d;
            var position = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale);
            if (position.X < 0 || position.Y < 0 ||
                position.X >= _root.ActualWidth || position.Y >= _root.ActualHeight)
            {
                SetHoveredButton(null);
                return;
            }

            SetHoveredButton(DisabledButtonAt(position));
        }

        private ButtonBase? DisabledButtonAt(Windows.Foundation.Point position)
        {
            if (_buttonCache.Count == 0 ||
                (_buttonCacheDirty && Environment.TickCount64 - _lastButtonCacheRefresh >= 250))
                RefreshButtonCache();

            foreach (var button in _buttonCache)
            {
                if (!button.IsEnabled &&
                    button.Visibility == Visibility.Visible &&
                    Contains(button, position))
                    return button;
            }

            return null;
        }

        private void RefreshButtonCache()
        {
            _buttonCache.Clear();
            CollectButtons(_root);

            if (_root.XamlRoot is { } xamlRoot)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                {
                    if (popup.Child is { } child)
                        CollectButtons(child);
                }
            }

            _lastButtonCacheRefresh = Environment.TickCount64;
            _buttonCacheDirty = false;
        }

        private void CollectButtons(DependencyObject parent)
        {
            if (parent is ButtonBase button)
                _buttonCache.Add(button);

            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var index = 0; index < childCount; index++)
                CollectButtons(VisualTreeHelper.GetChild(parent, index));
        }

        private bool Contains(ButtonBase button, Windows.Foundation.Point position)
        {
            if (button.ActualWidth <= 0 || button.ActualHeight <= 0)
                return false;

            try
            {
                var topLeft = button.TransformToVisual(_root)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                return position.X >= topLeft.X &&
                       position.Y >= topLeft.Y &&
                       position.X < topLeft.X + button.ActualWidth &&
                       position.Y < topLeft.Y + button.ActualHeight;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private void SetHoveredButton(ButtonBase? button)
        {
            if (ReferenceEquals(_hoveredButton, button))
                return;

            var previous = _hoveredButton;
            var previousVisual = _hoverVisual;
            if (previous is not null)
                previous.IsEnabledChanged -= HoveredButton_IsEnabledChanged;

            _hoveredButton = button;
            _hoverVisual = button is null ? null : HoverVisual.Capture(button);
            if (_hoveredButton is not null)
                _hoveredButton.IsEnabledChanged += HoveredButton_IsEnabledChanged;

            QueueVisuals(previousVisual, _hoverVisual);
        }

        private void HoveredButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (sender is ButtonBase { IsEnabled: true })
                SetHoveredButton(null);
        }

        private void QueueVisuals(HoverVisual? previous, HoverVisual? current)
        {
            var version = ++_visualStateVersion;
            _root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_isDisposed || version != _visualStateVersion)
                    return;

                if (previous is not null && !ReferenceEquals(previous.Button, _hoveredButton))
                    previous.Restore();

                if (current is not null && ReferenceEquals(current.Button, _hoveredButton) && !current.Button.IsEnabled)
                    current.ApplyPointerOver();
            });
        }

        private sealed class HoverVisual
        {
            private readonly ContentPresenter _presenter;
            private readonly Brush? _background;
            private readonly Brush? _borderBrush;
            private readonly Brush? _foreground;
            private ToolTip? _toolTip;

            private HoverVisual(ButtonBase button, ContentPresenter presenter)
            {
                Button = button;
                _presenter = presenter;
                _background = presenter.Background;
                _borderBrush = presenter.BorderBrush;
                _foreground = presenter.Foreground;
            }

            public ButtonBase Button { get; }

            public static HoverVisual? Capture(ButtonBase button)
            {
                button.ApplyTemplate();
                var presenter = FindContentPresenter(button);
                return presenter is null ? null : new HoverVisual(button, presenter);
            }

            public void ApplyPointerOver()
            {
                VisualStateManager.GoToState(Button, "Normal", useTransitions: false);
                _presenter.Background = ResourceBrush(Button, "AppControlHoverBrush", _background);
                _presenter.BorderBrush = _borderBrush;
                _presenter.Foreground = _foreground;
                OpenToolTip();
            }

            public void Restore()
            {
                CloseToolTip();
                VisualStateManager.GoToState(Button, "Normal", useTransitions: false);
                _presenter.ClearValue(ContentPresenter.BackgroundProperty);
                _presenter.ClearValue(ContentPresenter.BorderBrushProperty);
                _presenter.ClearValue(ContentPresenter.ForegroundProperty);

                if (!Button.IsEnabled)
                    VisualStateManager.GoToState(Button, "Disabled", useTransitions: false);
            }

            private void OpenToolTip()
            {
                var content = ToolTipService.GetToolTip(Button);
                if (content is null)
                    return;

                _toolTip = content as ToolTip ?? new ToolTip { Content = content };
                if (!ReferenceEquals(content, _toolTip))
                    ToolTipService.SetToolTip(Button, _toolTip);

                _toolTip.PlacementTarget = Button;
                _toolTip.IsOpen = true;
            }

            private void CloseToolTip()
            {
                if (_toolTip is not null)
                    _toolTip.IsOpen = false;

                _toolTip = null;
            }

            private static ContentPresenter? FindContentPresenter(DependencyObject root)
            {
                var childCount = VisualTreeHelper.GetChildrenCount(root);
                for (var index = 0; index < childCount; index++)
                {
                    var child = VisualTreeHelper.GetChild(root, index);
                    if (child is ContentPresenter presenter)
                        return presenter;

                    var descendant = FindContentPresenter(child);
                    if (descendant is not null)
                        return descendant;
                }

                return null;
            }

            private static Brush? ResourceBrush(ButtonBase button, string key, Brush? fallback)
            {
                if (button.Resources.TryGetValue(key, out var local) && local is Brush localBrush)
                    return localBrush;

                if (Application.Current.Resources.TryGetValue(key, out var application) && application is Brush applicationBrush)
                    return applicationBrush;

                return fallback;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);
    }
}
