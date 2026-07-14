using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.ViewManagement;

namespace CoursePlanner.Services;

public enum AppAnimationProfile
{
    None,
    ContentRefresh,
    PaneFromLeft,
    PaneFromRight,
    DynamicChildren,
    Reorder,
    Interactive,
    Attention
}

public enum AppNavigationMotion
{
    Refresh,
    DrillIn
}

public enum AppHorizontalAnchor
{
    Left,
    Right
}

public enum AppContentDirection
{
    Backward = -1,
    Forward = 1
}

/// <summary>
/// Owns app-level motion policy while leaving standard control-state animation
/// to the native WinUI control templates.
/// </summary>
public static class AppAnimationLayer
{
    private static readonly TimeSpan FasterDuration = TimeSpan.FromMilliseconds(83);
    private static readonly TimeSpan FastDuration = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan PaneEntranceDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PaneExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan TransientBannerEntranceDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TransientBannerExitDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PaneCompletionGracePeriod = TimeSpan.FromMilliseconds(100);
    private const float PaneTravelDistance = 72;
    private const float TransientBannerTravelDistance = 16;
    private static readonly UISettings SystemUiSettings = new();
    private static readonly ConditionalWeakTable<UIElement, AnimationState> States = new();
    private static readonly ConditionalWeakTable<FlyoutBase, object> HookedFlyouts = new();

    static AppAnimationLayer()
    {
        if (!SupportsAnimationsEnabledChanged())
        {
            return;
        }

        try
        {
            SystemUiSettings.AnimationsEnabledChanged += SystemUiSettings_AnimationsEnabledChanged;
        }
        catch (COMException)
        {
            // WM_SETTINGCHANGE refreshes registered profiles when the WinRT
            // event source is unavailable to the desktop process.
        }
    }

    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    private static bool SupportsAnimationsEnabledChanged()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;

        return ApiInformation.IsEventPresent(
            "Windows.UI.ViewManagement.UISettings",
            "AnimationsEnabledChanged");
    }

    public static readonly DependencyProperty ProfileProperty =
        DependencyProperty.RegisterAttached(
            "Profile",
            typeof(AppAnimationProfile),
            typeof(AppAnimationLayer),
            new PropertyMetadata(AppAnimationProfile.None, OnProfileChanged));

    public static bool AnimationsEnabled => SystemUiSettings.AnimationsEnabled;

    public static AppAnimationProfile GetProfile(DependencyObject element) =>
        (AppAnimationProfile)element.GetValue(ProfileProperty);

    public static void SetProfile(DependencyObject element, AppAnimationProfile value) =>
        element.SetValue(ProfileProperty, value);

    public static void ConfigureFrame(Frame frame)
    {
        frame.ContentTransitions = new TransitionCollection
        {
            new NavigationThemeTransition()
        };
    }

    public static bool Navigate(
        Frame frame,
        Type pageType,
        object? parameter,
        AppNavigationMotion motion = AppNavigationMotion.Refresh)
    {
        if (frame.CurrentSourcePageType == pageType)
            return false;

        return frame.Navigate(pageType, parameter, Navigation(motion));
    }

    public static bool NavigateDirectional(
        Frame frame,
        Type pageType,
        object parameter,
        AppContentDirection direction)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(pageType);
        ArgumentNullException.ThrowIfNull(parameter);

        NavigationTransitionInfo transition = !AnimationsEnabled
            ? new SuppressNavigationTransitionInfo()
            : new SlideNavigationTransitionInfo
            {
                Effect = direction == AppContentDirection.Forward
                    ? SlideNavigationTransitionEffect.FromRight
                    : SlideNavigationTransitionEffect.FromLeft
            };
        return frame.Navigate(pageType, parameter, transition);
    }

    public static NavigationTransitionInfo Navigation(AppNavigationMotion motion) =>
        !AnimationsEnabled
            ? new SuppressNavigationTransitionInfo()
            : motion == AppNavigationMotion.DrillIn
                ? new DrillInNavigationTransitionInfo()
                : new EntranceNavigationTransitionInfo();

    /// <summary>
    /// Starts a versioned pane entrance before the caller makes it visible.
    /// The returned version lets later entrance/exit requests invalidate stale
    /// completion work without changing material-layer elevation.
    /// </summary>
    public static long PreparePaneEntrance(FrameworkElement pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var state = StateFor(pane);
        CancelPaneMotion(pane, state);
        var version = ++state.PaneVersion;
        CapturePaneBaseline(pane, state);
        state.PaneMotionRunning = true;
        state.PaneEntrancePrepared = true;

        ElementCompositionPreview.SetIsTranslationEnabled(pane, true);
        return version;
    }

    /// <summary>
    /// Starts explicit post-layout Composition animations immediately after
    /// the caller restores pane geometry and Visibility. This avoids an empty
    /// pre-animation frame and keeps the motion off the UI thread.
    /// </summary>
    public static async Task<bool> PlayPreparedPaneEntranceAsync(
        FrameworkElement pane,
        long version)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var state = StateFor(pane);
        if (!IsCurrentPreparedPaneEntrance(state, version))
            return false;

        if (!AnimationsEnabled || !pane.IsLoaded)
        {
            CompletePaneMotion(pane, state, version);
            return true;
        }

        var visual = ElementCompositionPreview.GetElementVisual(pane);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0, 0),
            new Vector2(0, 1));

        var translationAnimation = compositor.CreateScalarKeyFrameAnimation();
        translationAnimation.Target = "Translation.X";
        translationAnimation.Duration = PaneEntranceDuration;
        translationAnimation.InsertKeyFrame(
            0,
            state.PaneBaseTranslation.X + PaneTranslationX(pane));
        translationAnimation.InsertKeyFrame(1, state.PaneBaseTranslation.X, easing);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Target = "Opacity";
        opacityAnimation.Duration = PaneEntranceDuration;
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(1, (float)state.PaneBaseOpacity, easing);

        await PlayPaneCompositionAnimationAsync(
            pane,
            state,
            translationAnimation,
            opacityAnimation,
            PaneEntranceDuration,
            version);
        if (!IsCurrentPreparedPaneEntrance(state, version))
            return false;

        CompletePaneMotion(pane, state, version);
        return true;
    }

    /// <summary>
    /// Plays a pane exit while its last measured geometry is still available,
    /// then commits the caller's Collapsed/layout state only if this request is
    /// still current. A later entrance cancels the stale finalizer.
    /// </summary>
    public static async Task<bool> PlayPaneExitThenAsync(
        FrameworkElement pane,
        Action finalize)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(finalize);

        var state = StateFor(pane);
        CancelPaneMotion(pane, state);
        var version = ++state.PaneVersion;
        CapturePaneBaseline(pane, state);
        state.PaneMotionRunning = true;
        state.PaneEntrancePrepared = false;
        state.PendingPaneExitFinalize = finalize;

        if (!AnimationsEnabled || !pane.IsLoaded)
        {
            CompletePaneExit(pane, state, version);
            return true;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(pane, true);
        var visual = ElementCompositionPreview.GetElementVisual(pane);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(1, 0),
            new Vector2(1, 1));

        var translationAnimation = compositor.CreateScalarKeyFrameAnimation();
        translationAnimation.Target = "Translation.X";
        translationAnimation.Duration = PaneExitDuration;
        translationAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
        translationAnimation.InsertKeyFrame(
            1,
            state.PaneBaseTranslation.X + PaneTranslationX(pane),
            easing);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Target = "Opacity";
        opacityAnimation.Duration = PaneExitDuration;
        opacityAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacityAnimation.InsertKeyFrame(1, 0, easing);

        await PlayPaneCompositionAnimationAsync(
            pane,
            state,
            translationAnimation,
            opacityAnimation,
            PaneExitDuration,
            version);
        if (!IsCurrentPaneMotion(state, version))
            return false;

        CompletePaneExit(pane, state, version);
        return true;
    }

    /// <summary>
    /// Prepares a transient banner for a short upward-to-rest entrance. The
    /// existing versioned pane-motion state is reused so a replacement banner
    /// cancels stale entrance or exit completion work atomically.
    /// </summary>
    public static long PrepareTransientBannerEntrance(FrameworkElement banner)
    {
        ArgumentNullException.ThrowIfNull(banner);

        var state = StateFor(banner);
        CancelPaneMotion(banner, state);
        var version = ++state.PaneVersion;
        CapturePaneBaseline(banner, state);
        state.PaneMotionRunning = true;
        state.PaneEntrancePrepared = true;

        ElementCompositionPreview.SetIsTranslationEnabled(banner, true);
        return version;
    }

    public static async Task<bool> PlayPreparedTransientBannerEntranceAsync(
        FrameworkElement banner,
        long version)
    {
        ArgumentNullException.ThrowIfNull(banner);

        var state = StateFor(banner);
        if (!IsCurrentPreparedPaneEntrance(state, version))
            return false;

        if (!AnimationsEnabled || !banner.IsLoaded)
        {
            CompletePaneMotion(banner, state, version);
            return true;
        }

        var visual = ElementCompositionPreview.GetElementVisual(banner);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0, 0),
            new Vector2(0, 1));

        var translationAnimation = compositor.CreateScalarKeyFrameAnimation();
        translationAnimation.Target = "Translation.Y";
        translationAnimation.Duration = TransientBannerEntranceDuration;
        translationAnimation.InsertKeyFrame(
            0,
            state.PaneBaseTranslation.Y - TransientBannerTravelDistance);
        translationAnimation.InsertKeyFrame(1, state.PaneBaseTranslation.Y, easing);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Target = "Opacity";
        opacityAnimation.Duration = TransientBannerEntranceDuration;
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(1, (float)state.PaneBaseOpacity, easing);

        await PlayPaneCompositionAnimationAsync(
            banner,
            state,
            translationAnimation,
            opacityAnimation,
            TransientBannerEntranceDuration,
            version);
        if (!IsCurrentPreparedPaneEntrance(state, version))
            return false;

        CompletePaneMotion(banner, state, version);
        return true;
    }

    public static async Task<bool> PlayTransientBannerExitThenAsync(
        FrameworkElement banner,
        Action finalize)
    {
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(finalize);

        var state = StateFor(banner);
        var animate = AnimationsEnabled && banner.IsLoaded;
        var handedOff = animate && TryBeginPaneMotionHandoff(banner, state);
        if (!handedOff)
        {
            CancelPaneMotion(banner, state);
            CapturePaneBaseline(banner, state);
        }
        var version = ++state.PaneVersion;
        state.PaneMotionRunning = true;
        state.PaneEntrancePrepared = false;
        state.PendingPaneExitFinalize = finalize;

        if (!animate)
        {
            CompletePaneExit(banner, state, version);
            return true;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(banner, true);
        var visual = ElementCompositionPreview.GetElementVisual(banner);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(1, 0),
            new Vector2(1, 1));

        var translationAnimation = compositor.CreateScalarKeyFrameAnimation();
        translationAnimation.Target = "Translation.Y";
        translationAnimation.Duration = TransientBannerExitDuration;
        translationAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
        translationAnimation.InsertKeyFrame(
            1,
            state.PaneBaseTranslation.Y - TransientBannerTravelDistance,
            easing);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Target = "Opacity";
        opacityAnimation.Duration = TransientBannerExitDuration;
        opacityAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacityAnimation.InsertKeyFrame(1, 0, easing);

        await PlayPaneCompositionAnimationAsync(
            banner,
            state,
            translationAnimation,
            opacityAnimation,
            TransientBannerExitDuration,
            version);
        if (!IsCurrentPaneMotion(state, version))
            return false;

        CompletePaneExit(banner, state, version);
        return true;
    }

    public static void CancelTransientBannerMotion(FrameworkElement banner)
    {
        ArgumentNullException.ThrowIfNull(banner);
        CancelPaneMotion(banner, StateFor(banner));
    }

    /// <summary>
    /// Animates a tool window's WinUI visual tree before the owning window
    /// hides its HWND. Keeping this on the compositor avoids AnimateWindow's
    /// incorrect blending of WinUI non-client and Mica surfaces.
    /// </summary>
    public static async Task PlayToolWindowExitThenAsync(
        FrameworkElement root,
        Action finalize)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(finalize);

        try
        {
            if (!AnimationsEnabled || !root.IsLoaded)
                return;

            var visual = ElementCompositionPreview.GetElementVisual(root);
            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(1, 0),
                new Vector2(1, 1));

            var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Duration = PaneExitDuration;
            opacityAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
            opacityAnimation.InsertKeyFrame(1, 0, easing);

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            var watchdog = root.DispatcherQueue.CreateTimer();

            void BatchCompleted(object? sender, CompositionBatchCompletedEventArgs args) =>
                completion.TrySetResult(true);

            void WatchdogElapsed(DispatcherQueueTimer sender, object args) =>
                completion.TrySetResult(false);

            batch.Completed += BatchCompleted;
            watchdog.Interval = PaneExitDuration + PaneCompletionGracePeriod;
            watchdog.IsRepeating = false;
            watchdog.Tick += WatchdogElapsed;
            watchdog.Start();

            visual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);
            batch.End();

            try
            {
                await completion.Task;
            }
            finally
            {
                watchdog.Stop();
                watchdog.Tick -= WatchdogElapsed;
                batch.Completed -= BatchCompleted;
                batch.Dispose();
            }
        }
        finally
        {
            finalize();
        }
    }

    /// <summary>
    /// Cancels an exit whose delayed finalizer has not run yet. This is used
    /// when a user reopens the same pane during its exit animation, so the
    /// stale close request cannot discard the newly selected content.
    /// </summary>
    public static bool CancelPendingPaneExit(FrameworkElement pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var state = StateFor(pane);
        if (!state.PaneMotionRunning || state.PendingPaneExitFinalize is null)
            return false;

        state.PaneVersion++;
        RestorePaneBaseline(pane, state);
        return true;
    }

    public static bool CancelPreparedPaneEntrance(FrameworkElement pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var state = StateFor(pane);
        if (!state.PaneMotionRunning || !state.PaneEntrancePrepared)
            return false;

        state.PaneVersion++;
        RestorePaneBaseline(pane, state);
        return true;
    }

    /// <summary>
    /// Commits an in-flight exit immediately. Page teardown uses this to
    /// honor the close request before the old visual instance can outlive its
    /// view-model subscriptions and finalize against a later page instance.
    /// </summary>
    public static bool CompletePendingPaneExit(FrameworkElement pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var state = StateFor(pane);
        if (!state.PaneMotionRunning || state.PendingPaneExitFinalize is null)
            return false;

        CompletePaneExit(pane, state, state.PaneVersion);
        return true;
    }

    /// <summary>
    /// Applies the invert-and-play phase of a horizontal FLIP reflow. XAML
    /// commits the final layout immediately, while the compositor carries the
    /// element from its previous rendered position to the new one without
    /// blocking input or running per-frame layout work on the UI thread.
    /// Repeated requests retarget from the current animated translation.
    /// </summary>
    public static void PlayHorizontalReflow(FrameworkElement target, double layoutDeltaX)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = StateFor(target);
        if (!AnimationsEnabled || !target.IsLoaded || Math.Abs(layoutDeltaX) < 0.5)
        {
            ClearHorizontalReflow(target, state);
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(target, true);
        var compositor = ElementCompositionPreview.GetElementVisual(target).Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Translation.X";
        animation.Duration = FastDuration;
        var retargeting = state.HorizontalReflowAnimation is not null;
        if (retargeting)
        {
            animation.SetScalarParameter("layoutDelta", (float)layoutDeltaX);
            animation.InsertExpressionKeyFrame(0, "this.StartingValue + layoutDelta");
        }
        else
        {
            var translation = target.Translation;
            target.Translation = new Vector3(
                translation.X + (float)layoutDeltaX,
                translation.Y,
                translation.Z);
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        }
        animation.InsertKeyFrame(
            1,
            0,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0),
                new Vector2(0, 1)));

        var previousAnimation = state.HorizontalReflowAnimation;
        var version = ++state.HorizontalReflowVersion;
        state.HorizontalReflowAnimation = animation;
        target.StartAnimation(animation);
        previousAnimation?.Dispose();
        _ = FinishHorizontalReflowAfterDurationAsync(target, state, animation, version);
    }

    /// <summary>
    /// Smooths a docked layout width change without animating layout itself.
    /// The compositor scales the already-arranged center surface from its
    /// previous visual width to the new column width, anchored at the left.
    /// </summary>
    public static void PlayResponsiveWidthReflow(
        FrameworkElement target,
        double previousWidth,
        double currentWidth,
        AppHorizontalAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = StateFor(target);
        ClearResponsiveWidthReflow(state);
        if (!AnimationsEnabled ||
            !target.IsLoaded ||
            previousWidth <= 0 ||
            currentWidth <= 0 ||
            Math.Abs(previousWidth - currentWidth) < 1)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(target);
        var baseScale = visual.Scale;
        var baseCenterPoint = visual.CenterPoint;
        var widthRatio = (float)Math.Clamp(previousWidth / currentWidth, 0.72, 1.35);
        var duration = previousWidth > currentWidth
            ? PaneEntranceDuration
            : PaneExitDuration;
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = duration;
        animation.InsertKeyFrame(
            0,
            new Vector3(baseScale.X * widthRatio, baseScale.Y, baseScale.Z));
        animation.InsertKeyFrame(
            1,
            baseScale,
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0, 0),
                new Vector2(0, 1)));

        state.ResponsiveReflowVisual = visual;
        state.ResponsiveReflowBaseScale = baseScale;
        state.ResponsiveReflowBaseCenterPoint = baseCenterPoint;
        state.ResponsiveReflowAnimation = animation;
        var anchorX = anchor == AppHorizontalAnchor.Left ? 0 : (float)currentWidth;
        visual.CenterPoint = new Vector3(anchorX, baseCenterPoint.Y, baseCenterPoint.Z);
        visual.StartAnimation(nameof(Visual.Scale), animation);

        var timer = target.DispatcherQueue.CreateTimer();
        TypedEventHandler<DispatcherQueueTimer, object>? tick = null;
        tick = (sender, args) => ClearResponsiveWidthReflow(state);
        timer.Interval = duration + PaneCompletionGracePeriod;
        timer.IsRepeating = false;
        timer.Tick += tick;
        state.ResponsiveReflowTimer = timer;
        state.ResponsiveReflowTimerTick = tick;
        timer.Start();
    }

    /// <summary>
    /// Uses a compositor FLIP transform after the native HWND has adopted a
    /// new client size. The content starts at its previous visual dimensions
    /// and settles into the new layout without animating XAML layout itself.
    /// </summary>
    public static void PlayToolWindowSizeReflow(
        FrameworkElement target,
        double previousWidth,
        double previousHeight,
        double currentWidth,
        double currentHeight)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = StateFor(target);
        ClearResponsiveWidthReflow(state);
        if (!AnimationsEnabled ||
            !target.IsLoaded ||
            previousWidth <= 0 ||
            previousHeight <= 0 ||
            currentWidth <= 0 ||
            currentHeight <= 0 ||
            (Math.Abs(previousWidth - currentWidth) < 1 &&
             Math.Abs(previousHeight - currentHeight) < 1))
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(target);
        var baseScale = visual.Scale;
        var baseCenterPoint = visual.CenterPoint;
        var widthRatio = (float)Math.Clamp(previousWidth / currentWidth, 0.4, 2.5);
        var heightRatio = (float)Math.Clamp(previousHeight / currentHeight, 0.4, 2.5);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = PaneEntranceDuration;
        animation.InsertKeyFrame(
            0,
            new Vector3(
                baseScale.X * widthRatio,
                baseScale.Y * heightRatio,
                baseScale.Z));
        animation.InsertKeyFrame(
            1,
            baseScale,
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0, 0),
                new Vector2(0, 1)));

        state.ResponsiveReflowVisual = visual;
        state.ResponsiveReflowBaseScale = baseScale;
        state.ResponsiveReflowBaseCenterPoint = baseCenterPoint;
        state.ResponsiveReflowAnimation = animation;
        visual.CenterPoint = new Vector3(0, 0, baseCenterPoint.Z);
        visual.StartAnimation(nameof(Visual.Scale), animation);

        var timer = target.DispatcherQueue.CreateTimer();
        TypedEventHandler<DispatcherQueueTimer, object>? tick = null;
        tick = (sender, args) => ClearResponsiveWidthReflow(state);
        timer.Interval = PaneEntranceDuration + PaneCompletionGracePeriod;
        timer.IsRepeating = false;
        timer.Tick += tick;
        state.ResponsiveReflowTimer = timer;
        state.ResponsiveReflowTimerTick = tick;
        timer.Start();
    }

    public static void CancelResponsiveWidthReflow(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (States.TryGetValue(target, out var state))
            ClearResponsiveWidthReflow(state);
    }

    public static void ConfigureFlyout(FlyoutBase flyout)
    {
        flyout.AreOpenCloseAnimationsEnabled = AnimationsEnabled;
        if (HookedFlyouts.TryGetValue(flyout, out _))
            return;

        HookedFlyouts.Add(flyout, new object());
        flyout.Opening += Flyout_Opening;
        flyout.Closing += Flyout_Closing;
    }

    public static void RefreshPolicy()
    {
        foreach (var pair in States)
        {
            var state = pair.Value;
            if (state.Target is null ||
                !state.Target.TryGetTarget(out var target) ||
                state.DispatcherQueue is null)
            {
                continue;
            }

            if (state.DispatcherQueue.HasThreadAccess)
            {
                ApplyProfile(target, GetProfile(target));
                continue;
            }

            state.DispatcherQueue.TryEnqueue(() =>
            {
                if (state.Target.TryGetTarget(out var queuedTarget))
                    ApplyProfile(queuedTarget, GetProfile(queuedTarget));
            });
        }
    }

    private static void SystemUiSettings_AnimationsEnabledChanged(
        UISettings sender,
        UISettingsAnimationsEnabledChangedEventArgs args) =>
        RefreshPolicy();

    private static void Flyout_Opening(object? sender, object args)
    {
        if (sender is FlyoutBase flyout)
            flyout.AreOpenCloseAnimationsEnabled = AnimationsEnabled;
    }

    private static void Flyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args) =>
        sender.AreOpenCloseAnimationsEnabled = AnimationsEnabled;

    /// <summary>
    /// Replaces a large content subtree synchronously, then uses a native
    /// compositor opacity animation to reveal every refreshed state.
    /// </summary>
    public static void RefreshContent(FrameworkElement host, Action replace)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(replace);

        var state = StateFor(host);
        ClearRefreshAnimation(host, state);
        var refreshVersion = ++state.RefreshVersion;
        Cancel(host);
        replace();
        if (!AnimationsEnabled || !host.IsLoaded)
            return;

        // A queued XAML 0 -> 1 opacity change can be coalesced into one frame,
        // which makes subsequent refreshes appear static. Starting a fresh
        // compositor animation for every request gives each refresh its own
        // timeline and remains repeatable on the same loaded element.
        var visual = ElementCompositionPreview.GetElementVisual(host);
        var baselineOpacity = (float)Math.Clamp(host.Opacity, 0, 1);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = baselineOpacity;

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = FastDuration;
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(
            1,
            baselineOpacity,
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0),
                new Vector2(0, 1)));

        state.RefreshVisual = visual;
        state.RefreshBaselineOpacity = baselineOpacity;
        state.RefreshAnimationRunning = true;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
        _ = FinishRefreshAfterDurationAsync(host, state, refreshVersion);
    }

    /// <summary>
    /// Plays a short, non-blocking emphasis pulse. Repeated requests cancel the
    /// previous pulse and always restore the target's original visual values.
    /// </summary>
    public static Task PlayAttentionAsync(
        UIElement target,
        CancellationToken cancellationToken = default) =>
        PlayAttentionCoreAsync(target, null, null, cancellationToken);

    public static Task PlayAttentionAsync(
        Border target,
        Brush highlightBorder,
        Thickness highlightThickness,
        CancellationToken cancellationToken = default) =>
        PlayAttentionCoreAsync(
            target,
            highlightBorder,
            highlightThickness,
            cancellationToken);

    private static async Task PlayAttentionCoreAsync(
        UIElement target,
        Brush? highlightBorder,
        Thickness? highlightThickness,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = Begin(target, cancellationToken);
        var version = state.Version;
        if (target is Border border &&
            highlightBorder is not null &&
            highlightThickness is { } thickness)
        {
            state.HasBorderBaseline = true;
            state.BaseBorderBrush = border.BorderBrush;
            state.BaseBorderThickness = border.BorderThickness;
            border.BorderBrush = highlightBorder;
            border.BorderThickness = thickness;
        }

        try
        {
            if (!AnimationsEnabled)
            {
                await Task.Delay(FastDuration, state.Cancellation!.Token);
                return;
            }

            target.CenterPoint = new Vector3(
                (float)(target.ActualSize.X / 2),
                (float)(target.ActualSize.Y / 2),
                state.BaseCenterPoint.Z);
            target.ScaleTransition ??= new Vector3Transition
            {
                Components = Vector3TransitionComponents.X | Vector3TransitionComponents.Y,
                Duration = FasterDuration
            };
            target.OpacityTransition ??= new ScalarTransition
            {
                Duration = FasterDuration
            };

            target.Scale = new Vector3(
                state.BaseScale.X * 1.025f,
                state.BaseScale.Y * 1.025f,
                state.BaseScale.Z);
            target.Opacity = Math.Max(0.72, state.BaseOpacity * 0.82);
            await Task.Delay(FasterDuration, state.Cancellation!.Token);

            target.Scale = state.BaseScale;
            target.Opacity = state.BaseOpacity;
            await Task.Delay(FastDuration, state.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Complete(target, state, version);
        }
    }

    public static void Cancel(UIElement target)
    {
        if (!States.TryGetValue(target, out var state))
            return;
        if (state.Cancellation is null)
            return;

        state.Version++;
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose();
        state.Cancellation = null;
        Restore(target, state);
    }

    private static void OnProfileChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not UIElement element || args.NewValue is not AppAnimationProfile profile)
            return;

        StateFor(element);
        if (element is FrameworkElement frameworkElement)
            SetInteractiveHooks(frameworkElement, profile == AppAnimationProfile.Interactive);
        ApplyProfile(element, profile);
    }

    private static void ApplyProfile(UIElement element, AppAnimationProfile profile)
    {
        var state = StateFor(element);
        if (!AnimationsEnabled)
        {
            CompletePaneMotionForPolicy(element, state);
            ClearHorizontalReflow(element, state);
            Cancel(element);
        }
        RemoveOwnedProfileMotion(element, state);
        ClearRefreshAnimation(element, state);
        if (!AnimationsEnabled)
        {
            if (profile == AppAnimationProfile.Interactive)
                element.Scale = state.InteractiveBaseScale;
            return;
        }

        switch (profile)
        {
            case AppAnimationProfile.ContentRefresh:
                // RefreshContent owns the compositor opacity animation. Avoid
                // stacking a semantic content-replacement transition here.
                break;

            case AppAnimationProfile.PaneFromLeft:
            case AppAnimationProfile.PaneFromRight:
                // Both side panes use the explicit, replayable entrance and
                // exit API so docked reflow and overlay hit testing share the
                // same committed XAML geometry.
                break;

            case AppAnimationProfile.DynamicChildren:
                AddCollectionTransitions(element, state, includeReorder: false);
                break;

            case AppAnimationProfile.Reorder:
                AddCollectionTransitions(element, state, includeReorder: true);
                break;

            case AppAnimationProfile.Interactive:
                SetOwnedScaleTransition(element, state, new Vector3Transition
                {
                    Components = Vector3TransitionComponents.X | Vector3TransitionComponents.Y,
                    Duration = FasterDuration
                });
                UpdateCenterPoint(element);
                break;

            case AppAnimationProfile.Attention:
                SetOwnedScaleTransition(element, state, new Vector3Transition
                {
                    Components = Vector3TransitionComponents.X | Vector3TransitionComponents.Y,
                    Duration = FasterDuration
                });
                SetOwnedOpacityTransition(
                    element,
                    state,
                    new ScalarTransition { Duration = FasterDuration });
                break;

            case AppAnimationProfile.None:
            default:
                break;
        }
    }

    private static void AddCollectionTransitions(
        UIElement element,
        AnimationState state,
        bool includeReorder)
    {
        var transitions = element switch
        {
            Panel panel => panel.ChildrenTransitions,
            ItemsControl itemsControl => itemsControl.ItemContainerTransitions,
            _ => element.Transitions
        };

        AddOwnedTransition(state, transitions, new AddDeleteThemeTransition());
        AddOwnedTransition(state, transitions, new RepositionThemeTransition());
        if (includeReorder)
            AddOwnedTransition(state, transitions, new ReorderThemeTransition());
    }

    private static void AddOwnedTransition(
        AnimationState state,
        TransitionCollection collection,
        Transition transition)
    {
        collection.Add(transition);
        state.OwnedTransitions.Add(new OwnedTransition(collection, transition));
    }

    private static void RemoveOwnedProfileMotion(UIElement element, AnimationState state)
    {
        foreach (var owned in state.OwnedTransitions)
        {
            for (var index = owned.Collection.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(owned.Collection[index], owned.Transition))
                {
                    owned.Collection.RemoveAt(index);
                    break;
                }
            }
        }

        state.OwnedTransitions.Clear();
        if (state.OwnedScaleTransition is not null &&
            ReferenceEquals(element.ScaleTransition, state.OwnedScaleTransition))
        {
            element.ScaleTransition = state.OriginalScaleTransition;
        }

        if (state.OwnedOpacityTransition is not null &&
            ReferenceEquals(element.OpacityTransition, state.OwnedOpacityTransition))
        {
            element.OpacityTransition = state.OriginalOpacityTransition;
        }

        state.OwnedScaleTransition = null;
        state.OriginalScaleTransition = null;
        state.OwnedOpacityTransition = null;
        state.OriginalOpacityTransition = null;
    }

    private static void SetOwnedScaleTransition(
        UIElement element,
        AnimationState state,
        Vector3Transition transition)
    {
        state.OriginalScaleTransition = element.ScaleTransition;
        state.OwnedScaleTransition = transition;
        element.ScaleTransition = transition;
    }

    private static void SetOwnedOpacityTransition(
        UIElement element,
        AnimationState state,
        ScalarTransition transition)
    {
        state.OriginalOpacityTransition = element.OpacityTransition;
        state.OwnedOpacityTransition = transition;
        element.OpacityTransition = transition;
    }

    private static void ClearRefreshAnimation(UIElement element, AnimationState state)
    {
        state.RefreshVersion++;
        if (!state.RefreshAnimationRunning || state.RefreshVisual is null)
            return;

        state.RefreshVisual.StopAnimation(nameof(Visual.Opacity));
        state.RefreshVisual.Opacity = state.RefreshBaselineOpacity;
        state.RefreshVisual = null;
        state.RefreshAnimationRunning = false;
    }

    private static async Task FinishHorizontalReflowAfterDurationAsync(
        UIElement target,
        AnimationState state,
        ScalarKeyFrameAnimation animation,
        long version)
    {
        await Task.Delay(FastDuration + PaneCompletionGracePeriod);
        if (state.HorizontalReflowVersion != version ||
            !ReferenceEquals(state.HorizontalReflowAnimation, animation))
        {
            return;
        }

        ClearHorizontalReflow(target, state);
    }

    private static void ClearHorizontalReflow(UIElement target, AnimationState state)
    {
        state.HorizontalReflowVersion++;
        if (state.HorizontalReflowAnimation is null)
            return;

        target.StopAnimation(state.HorizontalReflowAnimation);
        state.HorizontalReflowAnimation.Dispose();
        state.HorizontalReflowAnimation = null;
        var translation = target.Translation;
        target.Translation = new Vector3(0, translation.Y, translation.Z);
    }

    private static float PaneTranslationX(FrameworkElement pane) =>
        GetProfile(pane) == AppAnimationProfile.PaneFromLeft
            ? -PaneTravelDistance
            : PaneTravelDistance;

    private static async Task PlayPaneCompositionAnimationAsync(
        FrameworkElement pane,
        AnimationState state,
        ScalarKeyFrameAnimation translationAnimation,
        ScalarKeyFrameAnimation opacityAnimation,
        TimeSpan duration,
        long version)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var visual = ElementCompositionPreview.GetElementVisual(pane);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var completionWatchdog = state.DispatcherQueue?.CreateTimer();
        void CompleteOnUiThread()
        {
            if (state.DispatcherQueue?.HasThreadAccess == true)
            {
                completion.TrySetResult(true);
                return;
            }

            if (state.DispatcherQueue?.TryEnqueue(
                    () => completion.TrySetResult(true)) != true)
            {
                completion.TrySetResult(false);
            }
        }

        void BatchCompleted(object? sender, CompositionBatchCompletedEventArgs args) =>
            CompleteOnUiThread();

        void WatchdogElapsed(DispatcherQueueTimer sender, object args) =>
            completion.TrySetResult(false);

        state.PaneTranslationAnimation = translationAnimation;
        state.PaneOpacityAnimation = opacityAnimation;
        state.PaneAnimationVisual = visual;
        state.PaneCompositionCompletion = completion;
        batch.Completed += BatchCompleted;
        if (completionWatchdog is not null)
        {
            completionWatchdog.Interval = duration + PaneCompletionGracePeriod;
            completionWatchdog.IsRepeating = false;
            completionWatchdog.Tick += WatchdogElapsed;
            completionWatchdog.Start();
        }

        pane.StartAnimation(translationAnimation);
        visual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);
        batch.End();

        try
        {
            await completion.Task;
        }
        finally
        {
            if (completionWatchdog is not null)
            {
                completionWatchdog.Stop();
                completionWatchdog.Tick -= WatchdogElapsed;
            }
            batch.Completed -= BatchCompleted;
            batch.Dispose();
        }

        if (state.PaneVersion == version &&
            ReferenceEquals(state.PaneCompositionCompletion, completion))
        {
            state.PaneCompositionCompletion = null;
        }
    }

    private static void StopPaneCompositionAnimations(
        FrameworkElement pane,
        AnimationState state)
    {
        var translationAnimation = state.PaneTranslationAnimation;
        var opacityAnimation = state.PaneOpacityAnimation;
        var visual = state.PaneAnimationVisual;
        var completion = state.PaneCompositionCompletion;
        state.PaneTranslationAnimation = null;
        state.PaneOpacityAnimation = null;
        state.PaneAnimationVisual = null;
        state.PaneCompositionCompletion = null;

        if (translationAnimation is not null)
            pane.StopAnimation(translationAnimation);
        if (opacityAnimation is not null && visual is not null)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = (float)state.PaneBaseOpacity;
        }
        completion?.TrySetResult(false);
    }

    private static void ClearResponsiveWidthReflow(AnimationState state)
    {
        if (state.ResponsiveReflowTimer is not null)
        {
            state.ResponsiveReflowTimer.Stop();
            if (state.ResponsiveReflowTimerTick is not null)
                state.ResponsiveReflowTimer.Tick -= state.ResponsiveReflowTimerTick;
        }

        if (state.ResponsiveReflowVisual is not null)
        {
            state.ResponsiveReflowVisual.StopAnimation(nameof(Visual.Scale));
            state.ResponsiveReflowVisual.Scale = state.ResponsiveReflowBaseScale;
            state.ResponsiveReflowVisual.CenterPoint = state.ResponsiveReflowBaseCenterPoint;
        }

        state.ResponsiveReflowTimer = null;
        state.ResponsiveReflowTimerTick = null;
        state.ResponsiveReflowVisual = null;
        state.ResponsiveReflowAnimation = null;
    }

    private static void CapturePaneBaseline(FrameworkElement pane, AnimationState state)
    {
        state.PaneBaseTranslation = pane.Translation;
        state.PaneBaseOpacity = pane.Opacity;
    }

    // Pane motion owns X/Y. Side-pane materials deliberately avoid a Z
    // translation so XAML hit-testing and the compositor share one geometry.
    private static Vector3 PaneBaselinePosition(
        FrameworkElement pane,
        AnimationState state) =>
        new(
            state.PaneBaseTranslation.X,
            state.PaneBaseTranslation.Y,
            pane.Translation.Z);

    private static bool IsCurrentPreparedPaneEntrance(AnimationState state, long version) =>
        IsCurrentPaneMotion(state, version) && state.PaneEntrancePrepared;

    private static bool IsCurrentPaneMotion(AnimationState state, long version) =>
        state.PaneMotionRunning && state.PaneVersion == version;

    private static void CancelPaneMotion(FrameworkElement pane, AnimationState state)
    {
        if (!state.PaneMotionRunning)
            return;

        state.PaneVersion++;
        RestorePaneBaseline(pane, state);
    }

    /// <summary>
    /// Retargets an in-flight composition animation without first snapping its
    /// animated properties back to their stable XAML baseline. The replacement
    /// animation can consequently use this.StartingValue as a continuous
    /// handoff point while the old generation and finalizer are invalidated.
    /// </summary>
    private static bool TryBeginPaneMotionHandoff(
        FrameworkElement pane,
        AnimationState state)
    {
        if (!state.PaneMotionRunning)
            return false;

        state.PaneVersion++;
        StopPaneCompositionAnimationsForHandoff(pane, state);
        state.PaneEntrancePrepared = false;
        state.PendingPaneExitFinalize = null;
        return true;
    }

    private static void StopPaneCompositionAnimationsForHandoff(
        FrameworkElement pane,
        AnimationState state)
    {
        var translationAnimation = state.PaneTranslationAnimation;
        var opacityAnimation = state.PaneOpacityAnimation;
        var visual = state.PaneAnimationVisual;
        var completion = state.PaneCompositionCompletion;
        state.PaneTranslationAnimation = null;
        state.PaneOpacityAnimation = null;
        state.PaneAnimationVisual = null;
        state.PaneCompositionCompletion = null;

        // LeaveCurrentValue materializes the compositor's presentation value
        // as the new animation's this.StartingValue. Do not write either XAML
        // property here: the stable baseline is restored only after exit.
        if (translationAnimation is not null)
        {
            translationAnimation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            pane.StopAnimation(translationAnimation);
        }
        if (opacityAnimation is not null && visual is not null)
        {
            opacityAnimation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            visual.StopAnimation(nameof(Visual.Opacity));
        }

        completion?.TrySetResult(false);
    }

    private static void CompletePaneMotion(
        FrameworkElement pane,
        AnimationState state,
        long version)
    {
        if (!IsCurrentPaneMotion(state, version))
            return;

        RestorePaneBaseline(pane, state);
    }

    private static void CompletePaneExit(
        FrameworkElement pane,
        AnimationState state,
        long version)
    {
        if (!IsCurrentPaneMotion(state, version))
            return;

        var finalize = state.PendingPaneExitFinalize;
        // Disconnect the backing-visual animations while the pane is still
        // in the live XAML tree. The finalizer may collapse it immediately.
        RestorePaneBaseline(pane, state);
        finalize?.Invoke();
    }

    private static void CompletePaneMotionForPolicy(UIElement element, AnimationState state)
    {
        if (!state.PaneMotionRunning || element is not FrameworkElement pane)
            return;

        state.PaneVersion++;
        var finalize = state.PendingPaneExitFinalize;
        RestorePaneBaseline(pane, state);
        finalize?.Invoke();
    }

    private static void RestorePaneBaseline(FrameworkElement pane, AnimationState state)
    {
        StopPaneCompositionAnimations(pane, state);
        pane.Translation = PaneBaselinePosition(pane, state);
        pane.Opacity = state.PaneBaseOpacity;
        state.PaneMotionRunning = false;
        state.PaneEntrancePrepared = false;
        state.PendingPaneExitFinalize = null;
    }

    private static async Task FinishRefreshAfterDurationAsync(
        UIElement element,
        AnimationState state,
        long refreshVersion)
    {
        await Task.Delay(FastDuration + TimeSpan.FromMilliseconds(34)).ConfigureAwait(false);
        var dispatcherQueue = state.DispatcherQueue;
        if (dispatcherQueue is null)
            return;

        if (dispatcherQueue.HasThreadAccess)
        {
            FinishRefresh(element, state, refreshVersion);
            return;
        }

        dispatcherQueue.TryEnqueue(() => FinishRefresh(element, state, refreshVersion));
    }

    private static void FinishRefresh(
        UIElement element,
        AnimationState state,
        long refreshVersion)
    {
        if (state.RefreshVersion != refreshVersion ||
            !state.RefreshAnimationRunning ||
            state.RefreshVisual is null)
            return;

        state.RefreshVisual.StopAnimation(nameof(Visual.Opacity));
        state.RefreshVisual.Opacity = state.RefreshBaselineOpacity;
        state.RefreshVisual = null;
        state.RefreshAnimationRunning = false;
    }

    private static void SetInteractiveHooks(FrameworkElement element, bool required)
    {
        var state = StateFor(element);
        if (state.InteractiveHooksInstalled == required)
            return;

        state.InteractiveHooksInstalled = required;
        if (required)
        {
            state.InteractiveBaseScale = element.Scale;
            element.Loaded += InteractiveElement_Loaded;
            element.SizeChanged += InteractiveElement_SizeChanged;
            element.PointerPressed += InteractiveElement_PointerPressed;
            element.PointerReleased += InteractiveElement_Reset;
            element.PointerExited += InteractiveElement_Reset;
            element.PointerCanceled += InteractiveElement_Reset;
            element.PointerCaptureLost += InteractiveElement_Reset;
            return;
        }

        element.Loaded -= InteractiveElement_Loaded;
        element.SizeChanged -= InteractiveElement_SizeChanged;
        element.PointerPressed -= InteractiveElement_PointerPressed;
        element.PointerReleased -= InteractiveElement_Reset;
        element.PointerExited -= InteractiveElement_Reset;
        element.PointerCanceled -= InteractiveElement_Reset;
        element.PointerCaptureLost -= InteractiveElement_Reset;
        element.Scale = state.InteractiveBaseScale;
    }

    private static void InteractiveElement_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
            UpdateCenterPoint(element);
    }

    private static void InteractiveElement_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
            UpdateCenterPoint(element);
    }

    private static void InteractiveElement_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is not UIElement element ||
            !AnimationsEnabled ||
            element is Control { IsEnabled: false })
        {
            return;
        }

        UpdateCenterPoint(element);
        var baseScale = StateFor(element).InteractiveBaseScale;
        element.Scale = new Vector3(
            baseScale.X * 0.985f,
            baseScale.Y * 0.985f,
            baseScale.Z);
    }

    private static void InteractiveElement_Reset(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is UIElement element)
            element.Scale = StateFor(element).InteractiveBaseScale;
    }

    private static void UpdateCenterPoint(UIElement element) =>
        element.CenterPoint = new Vector3(
            (float)(element.ActualSize.X / 2),
            (float)(element.ActualSize.Y / 2),
            element.CenterPoint.Z);

    private static AnimationState Begin(UIElement target, CancellationToken cancellationToken)
    {
        var state = StateFor(target);
        Cancel(target);
        state.Version++;
        state.BaseScale = target.Scale;
        state.BaseOpacity = target.Opacity;
        state.BaseCenterPoint = target.CenterPoint;
        state.BaseScaleTransition = target.ScaleTransition;
        state.BaseOpacityTransition = target.OpacityTransition;
        state.HasBorderBaseline = false;
        state.BaseBorderBrush = null;
        state.BaseBorderThickness = default;
        state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return state;
    }

    private static void Complete(UIElement target, AnimationState state, long version)
    {
        if (state.Version != version)
            return;

        Restore(target, state);
        state.Cancellation?.Dispose();
        state.Cancellation = null;
    }

    private static void Restore(UIElement target, AnimationState state)
    {
        target.Scale = state.BaseScale;
        target.Opacity = state.BaseOpacity;
        target.CenterPoint = state.BaseCenterPoint;
        target.ScaleTransition = state.BaseScaleTransition;
        target.OpacityTransition = state.BaseOpacityTransition;
        if (state.HasBorderBaseline && target is Border border)
        {
            border.BorderBrush = state.BaseBorderBrush;
            border.BorderThickness = state.BaseBorderThickness;
        }
    }

    private static AnimationState StateFor(UIElement target)
    {
        var state = States.GetOrCreateValue(target);
        state.Target ??= new WeakReference<UIElement>(target);
        state.DispatcherQueue ??= target.DispatcherQueue;
        return state;
    }

    private sealed class AnimationState
    {
        public WeakReference<UIElement>? Target { get; set; }
        public DispatcherQueue? DispatcherQueue { get; set; }
        public long Version { get; set; }
        public long RefreshVersion { get; set; }
        public long PaneVersion { get; set; }
        public long HorizontalReflowVersion { get; set; }
        public Vector3 BaseScale { get; set; } = Vector3.One;
        public double BaseOpacity { get; set; } = 1;
        public Vector3 BaseCenterPoint { get; set; }
        public Vector3Transition? BaseScaleTransition { get; set; }
        public ScalarTransition? BaseOpacityTransition { get; set; }
        public bool HasBorderBaseline { get; set; }
        public Brush? BaseBorderBrush { get; set; }
        public Thickness BaseBorderThickness { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public bool InteractiveHooksInstalled { get; set; }
        public Vector3 InteractiveBaseScale { get; set; } = Vector3.One;
        public Visual? RefreshVisual { get; set; }
        public float RefreshBaselineOpacity { get; set; } = 1;
        public bool RefreshAnimationRunning { get; set; }
        public ScalarKeyFrameAnimation? HorizontalReflowAnimation { get; set; }
        public bool PaneMotionRunning { get; set; }
        public bool PaneEntrancePrepared { get; set; }
        public ScalarKeyFrameAnimation? PaneTranslationAnimation { get; set; }
        public ScalarKeyFrameAnimation? PaneOpacityAnimation { get; set; }
        public Visual? PaneAnimationVisual { get; set; }
        public TaskCompletionSource<bool>? PaneCompositionCompletion { get; set; }
        public Vector3 PaneBaseTranslation { get; set; }
        public double PaneBaseOpacity { get; set; } = 1;
        public Action? PendingPaneExitFinalize { get; set; }
        public Visual? ResponsiveReflowVisual { get; set; }
        public Vector3 ResponsiveReflowBaseScale { get; set; } = Vector3.One;
        public Vector3 ResponsiveReflowBaseCenterPoint { get; set; }
        public Vector3KeyFrameAnimation? ResponsiveReflowAnimation { get; set; }
        public DispatcherQueueTimer? ResponsiveReflowTimer { get; set; }
        public TypedEventHandler<DispatcherQueueTimer, object>? ResponsiveReflowTimerTick { get; set; }
        public List<OwnedTransition> OwnedTransitions { get; } = [];
        public Vector3Transition? OriginalScaleTransition { get; set; }
        public Vector3Transition? OwnedScaleTransition { get; set; }
        public ScalarTransition? OriginalOpacityTransition { get; set; }
        public ScalarTransition? OwnedOpacityTransition { get; set; }
    }

    private sealed record OwnedTransition(
        TransitionCollection Collection,
        Transition Transition);
}
