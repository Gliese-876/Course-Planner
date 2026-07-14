using CoursePlanner.Core;

namespace CoursePlanner.Services;

/// <summary>
/// Canonical, UI-independent state for the meeting editor. Every value returned
/// from this type is exactly representable by the editor controls.
/// </summary>
public sealed class MeetingTimesEditorState
{
    private readonly List<MeetingTime> _meetings = new();
    private int _maxPeriod;
    private int _weekCount;

    public MeetingTimesEditorState(int maxPeriod = 20, int weekCount = 16)
    {
        _maxPeriod = NormalizePositive(maxPeriod);
        _weekCount = NormalizePositive(weekCount);
    }

    public int MaxPeriod
    {
        get => _maxPeriod;
        set
        {
            var normalized = NormalizePositive(value);
            if (_maxPeriod == normalized)
                return;

            _maxPeriod = normalized;
            NormalizeAllPeriods();
        }
    }

    public int WeekCount
    {
        get => _weekCount;
        set => _weekCount = NormalizePositive(value);
    }

    public void SetMeetings(IEnumerable<MeetingTime> meetings)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        _meetings.Clear();
        _meetings.AddRange(meetings.Select(CloneNormalized));
    }

    public IReadOnlyList<MeetingTime> GetMeetings() =>
        _meetings.Select(Clone).ToList();

    public bool AddMeeting()
    {
        if (_meetings.Count >= PlannerDataLimits.MaxMeetingsPerCourse)
            return false;

        _meetings.Add(new MeetingTime
        {
            Weekday = 1,
            StartPeriod = 1,
            EndPeriod = 1,
            Weeks = _weekCount == 1 ? "1" : $"1-{_weekCount}",
            WeekParity = WeekParity.All
        });
        return true;
    }

    public void RemoveMeeting(int index) => _meetings.RemoveAt(index);

    public void SetWeekday(int index, int weekday) =>
        _meetings[index].Weekday = Math.Clamp(weekday, 1, 7);

    public void SetStartPeriod(int index, int startPeriod)
    {
        var meeting = _meetings[index];
        meeting.StartPeriod = NormalizePeriod(startPeriod);
        meeting.EndPeriod = Math.Max(meeting.StartPeriod, NormalizePeriod(meeting.EndPeriod));
    }

    public void SetEndPeriod(int index, int endPeriod)
    {
        var meeting = _meetings[index];
        meeting.EndPeriod = Math.Max(meeting.StartPeriod, NormalizePeriod(endPeriod));
    }

    public void SetWeeks(int index, string? weeks) =>
        _meetings[index].Weeks = weeks ?? "";

    public void SetParity(int index, WeekParity parity) =>
        _meetings[index].WeekParity = NormalizeParity(parity);

    private void NormalizeAllPeriods()
    {
        for (var index = 0; index < _meetings.Count; index++)
        {
            var meeting = _meetings[index];
            meeting.StartPeriod = NormalizePeriod(meeting.StartPeriod);
            meeting.EndPeriod = Math.Max(meeting.StartPeriod, NormalizePeriod(meeting.EndPeriod));
        }
    }

    private MeetingTime CloneNormalized(MeetingTime meeting)
    {
        var start = NormalizePeriod(meeting.StartPeriod);
        return new MeetingTime
        {
            Weekday = Math.Clamp(meeting.Weekday, 1, 7),
            StartPeriod = start,
            EndPeriod = Math.Max(start, NormalizePeriod(meeting.EndPeriod)),
            Weeks = meeting.Weeks ?? "",
            WeekParity = NormalizeParity(meeting.WeekParity)
        };
    }

    private static MeetingTime Clone(MeetingTime meeting) => new()
    {
        Weekday = meeting.Weekday,
        StartPeriod = meeting.StartPeriod,
        EndPeriod = meeting.EndPeriod,
        Weeks = meeting.Weeks,
        WeekParity = meeting.WeekParity
    };

    private int NormalizePeriod(int value) => Math.Clamp(value, 1, _maxPeriod);

    private static int NormalizePositive(int value) => Math.Max(1, value);

    private static WeekParity NormalizeParity(WeekParity parity) =>
        Enum.IsDefined(parity) ? parity : WeekParity.All;
}
