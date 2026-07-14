namespace CoursePlanner.Core;

public static class PlanTabLimits
{
    public const int MaximumOpenPlans = 16;

    public static ValidationResult ValidateCanOpen(int openPlanCount, bool alreadyOpen)
    {
        if (openPlanCount < 0)
            throw new ArgumentOutOfRangeException(nameof(openPlanCount));

        var result = new ValidationResult();
        if (!alreadyOpen && openPlanCount >= MaximumOpenPlans)
            result.Error("OpenPlanTabsMaximum", MaximumOpenPlans.ToString());
        return result;
    }
}
