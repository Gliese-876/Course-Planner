using CoursePlanner.ViewModels;
using CoursePlanner.ToolWindows;

namespace CoursePlanner.Services;

public sealed class RegistrationOrderWindowService
{
    private readonly DocumentSession _documents;
    private readonly LocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly PlannerViewModel _planner;
    private readonly WindowInteropService _windowing;
    private readonly ToolWindowPlacementState _placement;
    private readonly BackgroundOperationService _backgroundOperations;
    private RegistrationOrderWindow? _window;

    public RegistrationOrderWindowService(
        DocumentSession documents,
        LocalizationService localization,
        ThemeService theme,
        PlannerViewModel planner,
        WindowInteropService windowing,
        ToolWindowPlacementState placement,
        BackgroundOperationService backgroundOperations)
    {
        _documents = documents;
        _localization = localization;
        _theme = theme;
        _planner = planner;
        _windowing = windowing;
        _placement = placement;
        _backgroundOperations = backgroundOperations;
        _backgroundOperations.Changed += BackgroundOperations_Changed;
    }

    public void Show(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        if (_backgroundOperations.IsBusy)
            return;

        if (_window is not null && string.Equals(_window.PlanId, planId, StringComparison.Ordinal))
        {
            _window.BringToFront();
            return;
        }

        Close();
        if (!_documents.Document.Plans.Any(plan =>
                string.Equals(plan.PlanId, planId, StringComparison.Ordinal)))
        {
            return;
        }

        var window = new RegistrationOrderWindow(
            _documents,
            _localization,
            _theme,
            _planner,
            _placement,
            planId,
            _windowing.Handle);
        _window = window;
        window.SetInteractionEnabled(!_backgroundOperations.IsBusy);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        window.Activate();
    }

    public bool IsOpenFor(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return _window is not null &&
               string.Equals(_window.PlanId, planId, StringComparison.Ordinal);
    }

    public async Task ToggleAsync(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        if (IsOpenFor(planId))
        {
            var window = _window;
            if (window is null)
                return;
            if (window.IsMinimized)
            {
                window.BringToFront();
                return;
            }

            await window.CloseAnimatedAsync();
            return;
        }

        Show(planId);
    }

    public void Close()
    {
        var window = _window;
        window?.HideAndClose();
        if (ReferenceEquals(_window, window))
            _window = null;
    }

    private void BackgroundOperations_Changed(object? sender, EventArgs e) =>
        _window?.SetInteractionEnabled(!_backgroundOperations.IsBusy);
}
