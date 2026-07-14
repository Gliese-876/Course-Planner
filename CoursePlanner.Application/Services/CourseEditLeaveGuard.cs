namespace CoursePlanner.Services;

public enum CourseEditLeaveChoice
{
    Save,
    Discard,
    Cancel
}

public static class CourseEditLeaveGuard
{
    public static async Task<bool> TryLeaveAsync(
        bool hasActiveEdit,
        bool hasUnsavedChanges,
        Func<Task<CourseEditLeaveChoice>> chooseAsync,
        Func<Task<bool>> saveAsync,
        Action discard)
    {
        ArgumentNullException.ThrowIfNull(chooseAsync);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(discard);

        if (!hasActiveEdit || !hasUnsavedChanges)
            return true;

        return await chooseAsync() switch
        {
            CourseEditLeaveChoice.Save => await saveAsync(),
            CourseEditLeaveChoice.Discard => DiscardAndContinue(discard),
            _ => false
        };
    }

    private static bool DiscardAndContinue(Action discard)
    {
        discard();
        return true;
    }
}

public sealed class CourseEditSessionState
{
    private string? _baselineFingerprint;

    public bool IsActive => _baselineFingerprint is not null;

    public void Begin(string baselineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(baselineFingerprint);
        _baselineFingerprint = baselineFingerprint;
    }

    public bool HasUnsavedChanges(string currentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(currentFingerprint);
        return IsActive && !string.Equals(_baselineFingerprint, currentFingerprint, StringComparison.Ordinal);
    }

    public void End() => _baselineFingerprint = null;
}
