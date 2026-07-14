using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class MeetingTimesEditorStateTests
{
    [Fact]
    public void RaisingStartPeriodAlsoRaisesEndPeriod()
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: 16);
        state.SetMeetings([Meeting(start: 2, end: 3)]);

        state.SetStartPeriod(0, 8);

        var meeting = Assert.Single(state.GetMeetings());
        Assert.Equal(8, meeting.StartPeriod);
        Assert.Equal(8, meeting.EndPeriod);
    }

    [Fact]
    public void LoweringEndPeriodCannotCreateAnInvertedRange()
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: 16);
        state.SetMeetings([Meeting(start: 5, end: 8)]);

        state.SetEndPeriod(0, 2);

        var meeting = Assert.Single(state.GetMeetings());
        Assert.Equal(5, meeting.StartPeriod);
        Assert.Equal(5, meeting.EndPeriod);
    }

    [Fact]
    public void LoadingOutOfRangePeriodsNormalizesTheCanonicalModel()
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: 16);

        state.SetMeetings([Meeting(start: 18, end: 20)]);

        var meeting = Assert.Single(state.GetMeetings());
        Assert.Equal(12, meeting.StartPeriod);
        Assert.Equal(12, meeting.EndPeriod);
    }

    [Fact]
    public void ReducingMaxPeriodRenormalizesExistingRows()
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: 16);
        state.SetMeetings([Meeting(start: 9, end: 11)]);

        state.MaxPeriod = 5;

        var meeting = Assert.Single(state.GetMeetings());
        Assert.Equal(5, meeting.StartPeriod);
        Assert.Equal(5, meeting.EndPeriod);
    }

    [Fact]
    public void InvalidParityIsNormalizedToTheVisibleDefault()
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: 16);
        var meeting = Meeting(start: 1, end: 1);
        meeting.WeekParity = (WeekParity)999;

        state.SetMeetings([meeting]);

        Assert.Equal(WeekParity.All, Assert.Single(state.GetMeetings()).WeekParity);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(8, "1-8")]
    [InlineData(20, "1-20")]
    public void NewRowsUseTheConfiguredSemesterWeekRange(int weekCount, string expected)
    {
        var state = new MeetingTimesEditorState(maxPeriod: 12, weekCount: weekCount);

        state.AddMeeting();

        Assert.Equal(expected, Assert.Single(state.GetMeetings()).Weeks);
    }

    [Fact]
    public void AddMeetingStopsAtTheSharedPersistenceCapacity()
    {
        var state = new MeetingTimesEditorState();

        for (var index = 0; index < PlannerDataLimits.MaxMeetingsPerCourse; index++)
            Assert.True(state.AddMeeting());

        Assert.False(state.AddMeeting());
        Assert.Equal(PlannerDataLimits.MaxMeetingsPerCourse, state.GetMeetings().Count);
    }

    private static MeetingTime Meeting(int start, int end) => new()
    {
        Weekday = 1,
        StartPeriod = start,
        EndPeriod = end,
        Weeks = "1-16",
        WeekParity = WeekParity.All
    };
}
