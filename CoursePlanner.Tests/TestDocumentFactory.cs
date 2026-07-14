using CoursePlanner.Core;

namespace CoursePlanner.Tests;

internal static class TestDocumentFactory
{
    public static PlannerDocument CreatePopulated()
    {
        var document = SeedData.Create();
        var semester = document.Semesters[0];
        var courses = new List<CourseOffering>
        {
            CreateCourse(semester.SemesterId, "Data Structures", "Prof. Lin", "Room A204", 3.5m, PlannerLabels.Major, PlannerLabels.Core, CourseColorService.Generate(0),
                68, 80, "Core algorithms and data structures.",
                new MeetingTime { Weekday = 1, StartPeriod = 3, EndPeriod = 4, Weeks = "1-16" },
                new MeetingTime { Weekday = 3, StartPeriod = 3, EndPeriod = 4, Weeks = "1-16" }),
            CreateCourse(semester.SemesterId, "Linear Algebra", "Prof. Wang", "Room B101", 4m, PlannerLabels.Major, PlannerLabels.Required, CourseColorService.Generate(1),
                90, 100, "Matrix methods and vector spaces.",
                new MeetingTime { Weekday = 2, StartPeriod = 1, EndPeriod = 2, Weeks = "1-16" },
                new MeetingTime { Weekday = 4, StartPeriod = 1, EndPeriod = 2, Weeks = "1-16" }),
            CreateCourse(semester.SemesterId, "Academic Writing", "Dr. Chen", "Room C302", 2m, PlannerLabels.General, PlannerLabels.Elective, CourseColorService.Generate(2),
                38, 40, "Writing workshop.",
                new MeetingTime { Weekday = 5, StartPeriod = 5, EndPeriod = 6, Weeks = "1-16", WeekParity = WeekParity.Odd }),
            CreateCourse(semester.SemesterId, "Human-Computer Interaction", "Dr. Zhao", "Design Lab", 2.5m, PlannerLabels.Major, PlannerLabels.Elective, CourseColorService.Generate(3),
                52, 60, "Studio course with project work.",
                new MeetingTime { Weekday = 5, StartPeriod = 7, EndPeriod = 8, Weeks = "2-16", WeekParity = WeekParity.Even }),
            CreateCourse(semester.SemesterId, "Physical Education", "Coach Liu", "Gym", 1m, PlannerLabels.Free, PlannerLabels.Elective, CourseColorService.Generate(4),
                24, 30, "",
                new MeetingTime { Weekday = 6, StartPeriod = 1, EndPeriod = 2, Weeks = "1-8,10,12-16" })
        };

        courses[0].Labels.AddRange([PlannerLabels.Morning, PlannerLabels.Project]);
        courses[1].Labels.Add(PlannerLabels.Morning);
        courses[3].Labels.Add(PlannerLabels.Project);
        document.CourseLibrary.AddRange(courses);

        var planA = document.Plans[0];
        planA.PlanName = "Balanced Plan";
        foreach (var course in courses.Take(4))
            planA.Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = course.OfferingId });

        var planB = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            PlanName = "Light Friday Plan",
            DisplayOrder = 1
        };
        foreach (var course in courses.Where(course => course.CourseName is "Data Structures" or "Linear Algebra" or "Physical Education"))
            planB.Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = course.OfferingId });
        document.Plans.Add(planB);

        document.Settings.CurrentPlanId = planA.PlanId;
        document.Settings.OpenPlanIds.Clear();
        document.Settings.OpenPlanIds.AddRange([planA.PlanId, planB.PlanId]);
        DocumentConsistencyService.Ensure(document);
        return document;
    }

    private static CourseOffering CreateCourse(
        string semesterId,
        string name,
        string teacher,
        string location,
        decimal credits,
        string? group,
        string? studyType,
        string color,
        int enrolled,
        int capacity,
        string notes,
        params MeetingTime[] meetings)
    {
        var course = new CourseOffering
        {
            SemesterId = semesterId,
            CourseName = name,
            Teacher = teacher,
            Location = location,
            Credits = credits,
            CourseGroupType = group,
            StudyType = studyType,
            EnrolledCount = enrolled,
            Capacity = capacity,
            Notes = notes,
            Color = color,
            MeetingTimes = meetings.ToList()
        };
        CourseIdentityService.AssignOfferingId(course);
        return course;
    }
}
