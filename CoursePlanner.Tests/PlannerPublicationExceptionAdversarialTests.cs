using System.Collections.Specialized;
using System.ComponentModel;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class PlannerPublicationExceptionAdversarialTests
{
    [Fact]
    public void OpeningAClosedPlanCannotBecomeUnpersistedAndUnretryableAfterAPublicationFailure()
    {
        using var fixture = Fixture.Create();
        var renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        var renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected OpenPlans subscriber failure.");
        fixture.ViewModel.OpenPlans.CollectionChanged += throwingSubscriber;
        fixture.ViewModel.OpenPlans.CollectionChanged += (_, _) =>
            renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
            renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);

        var firstFailure = Record.Exception(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        fixture.ViewModel.OpenPlans.CollectionChanged -= throwingSubscriber;
        var retrySucceeded = fixture.ViewModel.TryOpenPlan(
            fixture.TargetPlan,
            out var retryValidation);

        Assert.Equal(
            new PublicationOutcome(
                FirstFailureType: nameof(InvalidOperationException),
                SaveEvents: "planner.plan",
                CurrentSemester: "Target semester",
                CurrentPlan: "Target plan",
                DurableCurrentPlan: "Target plan",
                DurableOpenPlans: "Persisted plan|Target plan",
                InstalledOpenPlans: "Persisted plan|Target plan",
                RenderedOpenPlans: "Persisted plan|Target plan",
                InstalledGroupedCourses: "Target course",
                RenderedGroupedCourses: "Target course",
                RetrySucceeded: true,
                RetryWasValid: true),
            new PublicationOutcome(
                FirstFailureType: firstFailure?.GetType().Name,
                SaveEvents: string.Join('|', fixture.SaveEvents),
                CurrentSemester: fixture.ViewModel.CurrentSemester?.SemesterName,
                CurrentPlan: fixture.ViewModel.CurrentPlan?.PlanName,
                DurableCurrentPlan: CurrentPlanName(fixture.DurableDocument),
                DurableOpenPlans: OpenPlanNames(fixture.DurableDocument),
                InstalledOpenPlans: PlanNames(fixture.ViewModel.OpenPlans),
                RenderedOpenPlans: renderedOpenPlans,
                InstalledGroupedCourses: GroupedCourseNames(fixture.ViewModel),
                RenderedGroupedCourses: renderedGroupedCourses,
                RetrySucceeded: retrySucceeded,
                RetryWasValid: retryValidation.IsValid));
    }

    [Fact]
    public void FilterPublicationFailureCannotPermanentlyStrandOtherBindingsAtTheOldProjection()
    {
        using var fixture = Fixture.Create();
        var renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        var renderedSearchText = fixture.ViewModel.SearchText;
        var groupResetCount = 0;
        var searchPropertyChangedCount = 0;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected LibraryCourses subscriber failure.");
        fixture.ViewModel.LibraryCourses.CollectionChanged += throwingSubscriber;
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
        {
            groupResetCount++;
            renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PlannerViewModel.SearchText))
                return;

            searchPropertyChangedCount++;
            renderedSearchText = fixture.ViewModel.SearchText;
        };

        var firstFailure = Record.Exception(() =>
            fixture.ViewModel.SearchText = "Persisted course");

        fixture.ViewModel.LibraryCourses.CollectionChanged -= throwingSubscriber;
        fixture.ViewModel.SearchText = "Persisted course";

        Assert.Equal(
            new FilterPublicationOutcome(
                FirstFailureType: nameof(InvalidOperationException),
                InstalledSearchText: "Persisted course",
                RenderedSearchText: "Persisted course",
                InstalledGroupedCourses: "Persisted course",
                RenderedGroupedCourses: "Persisted course",
                GroupResetCount: 1,
                SearchPropertyChangedCount: 1),
            new FilterPublicationOutcome(
                FirstFailureType: firstFailure?.GetType().Name,
                InstalledSearchText: fixture.ViewModel.SearchText,
                RenderedSearchText: renderedSearchText,
                InstalledGroupedCourses: GroupedCourseNames(fixture.ViewModel),
                RenderedGroupedCourses: renderedGroupedCourses,
                GroupResetCount: groupResetCount,
                SearchPropertyChangedCount: searchPropertyChangedCount));
    }

    [Fact]
    public void RollbackPublicationFailureCannotStrandOtherProjectionBindingsAtTheRejectedState()
    {
        using var fixture = Fixture.Create();
        var renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        var renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        var laterRollbackSubscriberCalls = 0;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
        {
            if (fixture.ViewModel.CurrentSemester?.SemesterName == "Persisted semester")
            {
                throw new InvalidOperationException(
                    "Injected restored LibraryCourses subscriber failure.");
            }
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += throwingSubscriber;
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
            renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        fixture.ViewModel.OpenPlans.CollectionChanged += (_, _) =>
            renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        fixture.Session.RolledBack += (_, _) => laterRollbackSubscriberCalls++;
        fixture.FailPlanSave = true;

        var failure = Record.Exception(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        var failures = failure is null
            ? []
            : Flatten(failure).ToList();
        Assert.Equal(
            new RollbackPublicationOutcome(
                ContainsSaveFailure: true,
                ContainsPublicationFailure: true,
                CurrentSemester: "Persisted semester",
                CurrentPlan: "Persisted plan",
                InstalledOpenPlans: "Persisted plan",
                RenderedOpenPlans: "Persisted plan",
                InstalledGroupedCourses: "Alternate course|Persisted course",
                RenderedGroupedCourses: "Alternate course|Persisted course",
                LaterRollbackSubscriberCalls: 1,
                SessionConsistencyUnknown: false),
            new RollbackPublicationOutcome(
                ContainsSaveFailure: failures.Any(exception =>
                    exception is IOException &&
                    exception.Message.Contains("Injected planner.plan save failure", StringComparison.Ordinal)),
                ContainsPublicationFailure: failures.Any(exception =>
                    exception is InvalidOperationException &&
                    exception.Message.Contains("restored LibraryCourses", StringComparison.Ordinal)),
                CurrentSemester: fixture.ViewModel.CurrentSemester?.SemesterName,
                CurrentPlan: fixture.ViewModel.CurrentPlan?.PlanName,
                InstalledOpenPlans: PlanNames(fixture.ViewModel.OpenPlans),
                RenderedOpenPlans: renderedOpenPlans,
                InstalledGroupedCourses: GroupedCourseNames(fixture.ViewModel),
                RenderedGroupedCourses: renderedGroupedCourses,
                LaterRollbackSubscriberCalls: laterRollbackSubscriberCalls,
                SessionConsistencyUnknown: fixture.Session.IsSessionConsistencyUnknown));
    }

    [Fact]
    public void RollbackSemesterPublicationFailureStillPublishesEveryRemainingProjectionAndProperty()
    {
        using var fixture = Fixture.Create();
        var semesterResetCount = 0;
        var labelResetCount = 0;
        var allPlansResetCount = 0;
        var openPlansResetCount = 0;
        var libraryCoursesResetCount = 0;
        var libraryGroupsResetCount = 0;
        var currentContextPropertyCount = 0;
        var laterRollbackSubscriberCalls = 0;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
        {
            if (fixture.ViewModel.CurrentSemester?.SemesterName == "Persisted semester")
            {
                throw new InvalidOperationException(
                    "Injected restored Semesters subscriber failure.");
            }
        };
        fixture.ViewModel.Semesters.CollectionChanged += throwingSubscriber;
        fixture.ViewModel.Semesters.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                semesterResetCount++;
        };
        fixture.ViewModel.Labels.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                labelResetCount++;
        };
        fixture.ViewModel.AllPlans.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                allPlansResetCount++;
        };
        fixture.ViewModel.OpenPlans.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                openPlansResetCount++;
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                libraryCoursesResetCount++;
        };
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
                libraryGroupsResetCount++;
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (IsPersistedContext(fixture.ViewModel) &&
                args.PropertyName is nameof(PlannerViewModel.CurrentSemester) or
                    nameof(PlannerViewModel.CurrentPlan))
            {
                currentContextPropertyCount++;
            }
        };
        fixture.Session.RolledBack += (_, _) => laterRollbackSubscriberCalls++;
        fixture.FailPlanSave = true;

        var failure = Record.Exception(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));
        var failures = failure is null ? [] : Flatten(failure).ToList();

        Assert.Equal(
            new WholeProjectionRollbackOutcome(
                ContainsSaveFailure: true,
                ContainsSemesterPublicationFailure: true,
                SemesterResetCount: 1,
                LabelResetCount: 1,
                AllPlansResetCount: 1,
                OpenPlansResetCount: 1,
                LibraryCoursesResetCount: 1,
                LibraryGroupsResetCount: 1,
                CurrentContextPropertyCount: 2,
                LaterRollbackSubscriberCalls: 1,
                FinalContext: "Persisted semester|Persisted plan",
                FinalOpenPlans: "Persisted plan",
                FinalLibraryCourses: "Alternate course|Persisted course"),
            new WholeProjectionRollbackOutcome(
                ContainsSaveFailure: failures.Any(exception =>
                    exception is IOException &&
                    exception.Message.Contains("Injected planner.plan save failure", StringComparison.Ordinal)),
                ContainsSemesterPublicationFailure: failures.Any(exception =>
                    exception is InvalidOperationException &&
                    exception.Message.Contains("restored Semesters", StringComparison.Ordinal)),
                SemesterResetCount: semesterResetCount,
                LabelResetCount: labelResetCount,
                AllPlansResetCount: allPlansResetCount,
                OpenPlansResetCount: openPlansResetCount,
                LibraryCoursesResetCount: libraryCoursesResetCount,
                LibraryGroupsResetCount: libraryGroupsResetCount,
                CurrentContextPropertyCount: currentContextPropertyCount,
                LaterRollbackSubscriberCalls: laterRollbackSubscriberCalls,
                FinalContext: $"{fixture.ViewModel.CurrentSemester?.SemesterName}|{fixture.ViewModel.CurrentPlan?.PlanName}",
                FinalOpenPlans: PlanNames(fixture.ViewModel.OpenPlans),
                FinalLibraryCourses: GroupedCourseNames(fixture.ViewModel)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingAPlanCannotBecomeUnpersistedAndUnretryableAfterAPublicationFailure(
        bool persist)
    {
        using var fixture = Fixture.Create();
        Assert.True(fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out var openValidation));
        Assert.True(openValidation.IsValid);
        fixture.SaveEvents.Clear();
        var renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        var renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected close OpenPlans subscriber failure.");
        fixture.ViewModel.OpenPlans.CollectionChanged += throwingSubscriber;
        fixture.ViewModel.OpenPlans.CollectionChanged += (_, _) =>
            renderedOpenPlans = PlanNames(fixture.ViewModel.OpenPlans);
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
            renderedGroupedCourses = GroupedCourseNames(fixture.ViewModel);

        var failure = Record.Exception(() =>
            fixture.ViewModel.ClosePlanTab(fixture.TargetPlan, persist));

        fixture.ViewModel.OpenPlans.CollectionChanged -= throwingSubscriber;
        fixture.ViewModel.ClosePlanTab(fixture.TargetPlan);

        Assert.Equal(
            new ClosePublicationOutcome(
                FailureType: nameof(InvalidOperationException),
                SaveEvents: "plan.close-tab",
                CurrentContext: "Persisted semester|Persisted plan",
                DurableCurrentPlan: "Persisted plan",
                DurableOpenPlans: "Persisted plan",
                InstalledOpenPlans: "Persisted plan",
                RenderedOpenPlans: "Persisted plan",
                InstalledGroupedCourses: "Alternate course|Persisted course",
                RenderedGroupedCourses: "Alternate course|Persisted course"),
            new ClosePublicationOutcome(
                FailureType: failure?.GetType().Name,
                SaveEvents: string.Join('|', fixture.SaveEvents),
                CurrentContext: $"{fixture.ViewModel.CurrentSemester?.SemesterName}|{fixture.ViewModel.CurrentPlan?.PlanName}",
                DurableCurrentPlan: CurrentPlanName(fixture.DurableDocument),
                DurableOpenPlans: OpenPlanNames(fixture.DurableDocument),
                InstalledOpenPlans: PlanNames(fixture.ViewModel.OpenPlans),
                RenderedOpenPlans: renderedOpenPlans,
                InstalledGroupedCourses: GroupedCourseNames(fixture.ViewModel),
                RenderedGroupedCourses: renderedGroupedCourses));
    }

    [Fact]
    public void DeferredClosePublicationAndSaveFailureRestoresThenAllowsACompleteRetry()
    {
        using var fixture = Fixture.Create();
        Assert.True(fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out var openValidation));
        Assert.True(openValidation.IsValid);
        fixture.SaveEvents.Clear();
        fixture.FailCloseSave = true;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected deferred-close publication failure.");
        fixture.ViewModel.OpenPlans.CollectionChanged += throwingSubscriber;

        var firstFailure = Record.Exception(() =>
            fixture.ViewModel.ClosePlanTab(fixture.TargetPlan, persist: false));

        var firstFailures = firstFailure is null ? [] : Flatten(firstFailure).ToList();
        Assert.Contains(firstFailures, exception =>
            exception is InvalidOperationException &&
            exception.Message.Contains("deferred-close publication", StringComparison.Ordinal));
        Assert.Contains(firstFailures, exception =>
            exception is IOException &&
            exception.Message.Contains("plan.close-tab", StringComparison.Ordinal));
        Assert.Contains(fixture.ViewModel.OpenPlans, plan =>
            string.Equals(plan.PlanId, fixture.TargetPlan.PlanId, StringComparison.Ordinal));
        Assert.Contains(fixture.DurableDocument.Settings.OpenPlanIds, id =>
            string.Equals(id, fixture.TargetPlan.PlanId, StringComparison.Ordinal));

        fixture.ViewModel.OpenPlans.CollectionChanged -= throwingSubscriber;
        fixture.FailCloseSave = false;
        var retryTarget = fixture.ViewModel.OpenPlans.Single(plan =>
            string.Equals(plan.PlanId, fixture.TargetPlan.PlanId, StringComparison.Ordinal));
        fixture.ViewModel.ClosePlanTab(retryTarget, persist: false);
        fixture.ViewModel.PersistPlanTabState();

        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, plan =>
            string.Equals(plan.PlanId, fixture.TargetPlan.PlanId, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.DurableDocument.Settings.OpenPlanIds, id =>
            string.Equals(id, fixture.TargetPlan.PlanId, StringComparison.Ordinal));
        Assert.Equal("plan.close-tab|plan.close-tab", string.Join('|', fixture.SaveEvents));
    }

    [Fact]
    public void SavingACourseEditPublishesSelectionOnlyAfterTheEditIsDurable()
    {
        using var fixture = Fixture.Create();
        var source = fixture.Session.Document.CourseLibrary.Single(course =>
            course.CourseName == "Persisted course");
        var edited = JsonDefaults.Clone(source);
        edited.Notes = "durable edited notes";
        var detailPropertyChangedCount = 0;
        PropertyChangedEventHandler throwingSubscriber = (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.SelectedCourse))
            {
                throw new InvalidOperationException(
                    "Injected SelectedCourse subscriber failure.");
            }
        };
        fixture.ViewModel.PropertyChanged += throwingSubscriber;
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.IsDetailOpen))
                detailPropertyChangedCount++;
        };

        var failure = Record.Exception(() =>
            fixture.ViewModel.SaveLibraryCourseEdit(edited, source.OfferingId));

        Assert.Equal(
            new CourseEditPublicationOutcome(
                FailureType: nameof(InvalidOperationException),
                SaveEvents: "library.course-edit",
                DurableNotes: "durable edited notes",
                LiveNotes: "durable edited notes",
                SelectedCourseName: "Persisted course",
                IsDetailOpen: true,
                DetailPropertyChangedCount: 1),
            new CourseEditPublicationOutcome(
                FailureType: failure?.GetType().Name,
                SaveEvents: string.Join('|', fixture.SaveEvents),
                DurableNotes: fixture.DurableDocument.CourseLibrary.Single(course =>
                    course.CourseName == "Persisted course").Notes,
                LiveNotes: fixture.Session.Document.CourseLibrary.Single(course =>
                    course.CourseName == "Persisted course").Notes,
                SelectedCourseName: fixture.ViewModel.SelectedCourse?.CourseName,
                IsDetailOpen: fixture.ViewModel.IsDetailOpen,
                DetailPropertyChangedCount: detailPropertyChangedCount));
    }

    [Fact]
    public void AcceptedCourseEditStillPublishesSelectionWhenChangedRefreshReportsAnError()
    {
        using var fixture = Fixture.Create();
        var source = fixture.Session.Document.CourseLibrary.Single(course =>
            course.CourseName == "Persisted course");
        var edited = JsonDefaults.Clone(source);
        edited.Notes = "durable despite refresh error";
        var selectedPropertyChangedCount = 0;
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) =>
            throw new InvalidOperationException("Injected accepted refresh failure.");
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.SelectedCourse))
                selectedPropertyChangedCount++;
        };

        var failure = Record.Exception(() =>
            fixture.ViewModel.SaveLibraryCourseEdit(edited, source.OfferingId));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(["library.course-edit"], fixture.SaveEvents);
        Assert.Equal(
            "durable despite refresh error",
            fixture.DurableDocument.CourseLibrary.Single(course =>
                course.CourseName == "Persisted course").Notes);
        Assert.Equal("Persisted course", fixture.ViewModel.SelectedCourse?.CourseName);
        Assert.Equal(1, selectedPropertyChangedCount);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void KnownRollbackPublicationFailureCannotLeakItsSnapshotIntoTheNextFailureGeneration()
    {
        using var fixture = Fixture.Create();
        fixture.ViewModel.CurrentWeek = 5;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
        {
            if (IsPersistedContext(fixture.ViewModel))
            {
                throw new InvalidOperationException(
                    "Injected first-generation rollback publication failure.");
            }
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += throwingSubscriber;
        fixture.FailPlanSave = true;

        Assert.NotNull(Record.Exception(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _)));

        fixture.ViewModel.LibraryCourses.CollectionChanged -= throwingSubscriber;
        fixture.ViewModel.CurrentWeek = 8;
        Assert.Throws<IOException>(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        Assert.Equal(8, fixture.ViewModel.CurrentWeek);
        Assert.Equal("Persisted semester", fixture.ViewModel.CurrentSemester?.SemesterName);
        Assert.Equal("Persisted plan", fixture.ViewModel.CurrentPlan?.PlanName);
    }

    [Fact]
    public void CoordinatedCollectionPublicationUsesOneAudienceSnapshotForTheWholeBatch()
    {
        using var fixture = Fixture.Create();
        var earlyPropertyCalls = 0;
        var latePropertyCalls = 0;
        var firstCollectionCalls = 0;
        var removedCollectionCalls = 0;
        var propertyAddedCollectionCalls = 0;
        var collectionAddedCollectionCalls = 0;
        var propertyAudienceChanged = false;
        var collectionAudienceChanged = false;
        PropertyChangedEventHandler latePropertySubscriber = (_, _) => latePropertyCalls++;
        NotifyCollectionChangedEventHandler propertyAddedCollectionSubscriber = (_, _) =>
            propertyAddedCollectionCalls++;
        NotifyCollectionChangedEventHandler collectionAddedCollectionSubscriber = (_, _) =>
            collectionAddedCollectionCalls++;
        NotifyCollectionChangedEventHandler removedCollectionSubscriber = (_, _) =>
            removedCollectionCalls++;
        NotifyCollectionChangedEventHandler firstCollectionSubscriber = null!;
        firstCollectionSubscriber = (_, _) =>
        {
            firstCollectionCalls++;
            if (collectionAudienceChanged)
                return;

            collectionAudienceChanged = true;
            fixture.ViewModel.LibraryCourses.CollectionChanged -= removedCollectionSubscriber;
            fixture.ViewModel.LibraryCourses.CollectionChanged += collectionAddedCollectionSubscriber;
        };
        ((INotifyPropertyChanged)fixture.ViewModel.LibraryCourses).PropertyChanged += (_, args) =>
        {
            earlyPropertyCalls++;
            if (propertyAudienceChanged || args.PropertyName != nameof(fixture.ViewModel.LibraryCourses.Count))
                return;

            propertyAudienceChanged = true;
            ((INotifyPropertyChanged)fixture.ViewModel.LibraryCourses).PropertyChanged +=
                latePropertySubscriber;
            fixture.ViewModel.LibraryCourses.CollectionChanged +=
                propertyAddedCollectionSubscriber;
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += firstCollectionSubscriber;
        fixture.ViewModel.LibraryCourses.CollectionChanged += removedCollectionSubscriber;

        fixture.ViewModel.SearchText = "Persisted course";

        Assert.Equal(2, earlyPropertyCalls);
        Assert.Equal(0, latePropertyCalls);
        Assert.Equal(1, firstCollectionCalls);
        Assert.Equal(1, removedCollectionCalls);
        Assert.Equal(0, propertyAddedCollectionCalls);
        Assert.Equal(0, collectionAddedCollectionCalls);

        fixture.ViewModel.SearchText = "Alternate course";

        Assert.Equal(4, earlyPropertyCalls);
        Assert.Equal(2, latePropertyCalls);
        Assert.Equal(2, firstCollectionCalls);
        Assert.Equal(1, removedCollectionCalls);
        Assert.Equal(1, propertyAddedCollectionCalls);
        Assert.Equal(1, collectionAddedCollectionCalls);
    }

    [Fact]
    public void ReentrantPublicMutationWithMultipleSubscribersIsRejectedWithoutSkippingTheBatch()
    {
        using var fixture = Fixture.Create();
        var laterCourseSubscriberCalls = 0;
        var groupSubscriberCalls = 0;
        var filterPropertyCalls = 0;
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) =>
            fixture.ViewModel.LibraryCourses.Add(new CourseOffering
            {
                SemesterId = fixture.ViewModel.CurrentSemester?.SemesterId ?? "",
                CourseName = "must not be inserted"
            });
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) =>
            laterCourseSubscriberCalls++;
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
            groupSubscriberCalls++;
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.SearchText))
                filterPropertyCalls++;
        };

        var failure = Record.Exception(() =>
            fixture.ViewModel.SearchText = "Persisted course");

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, laterCourseSubscriberCalls);
        Assert.Equal(1, groupSubscriberCalls);
        Assert.Equal(1, filterPropertyCalls);
        Assert.Equal("Persisted course", fixture.ViewModel.SearchText);
        Assert.Equal("Persisted course", string.Join(
            '|',
            fixture.ViewModel.LibraryCourses.Select(course => course.CourseName)));
        Assert.Equal("Persisted course", GroupedCourseNames(fixture.ViewModel));
    }

    [Fact]
    public void CollectionPropertyFailureCannotSkipItemOrCollectionSignals()
    {
        using var fixture = Fixture.Create();
        var observedPropertyNames = new List<string?>();
        var collectionCalls = 0;
        ((INotifyPropertyChanged)fixture.ViewModel.LibraryCourses).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(fixture.ViewModel.LibraryCourses.Count))
                throw new InvalidOperationException("Injected Count subscriber failure.");
        };
        ((INotifyPropertyChanged)fixture.ViewModel.LibraryCourses).PropertyChanged += (_, args) =>
            observedPropertyNames.Add(args.PropertyName);
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) => collectionCalls++;

        var failure = Record.Exception(() =>
            fixture.ViewModel.SearchText = "Persisted course");

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(["Count", "Item[]"], observedPropertyNames);
        Assert.Equal(1, collectionCalls);
        Assert.Equal("Persisted course", fixture.ViewModel.SearchText);
        Assert.Equal("Persisted course", GroupedCourseNames(fixture.ViewModel));
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        switch (exception)
        {
            case DocumentSessionRollbackException rollback:
                foreach (var nested in Flatten(rollback.OperationException))
                    yield return nested;
                foreach (var nested in Flatten(rollback.RollbackException))
                    yield return nested;
                break;
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                    foreach (var nested in Flatten(inner))
                        yield return nested;
                break;
            default:
                if (exception.InnerException is not null)
                {
                    foreach (var nested in Flatten(exception.InnerException))
                        yield return nested;
                }
                break;
        }
    }

    private static string PlanNames(IEnumerable<SelectionPlan> plans) => string.Join(
        '|',
        plans.Select(plan => plan.PlanName).Order(StringComparer.Ordinal));

    private static string GroupedCourseNames(PlannerViewModel viewModel) => string.Join(
        '|',
        viewModel.LibraryGroups
            .SelectMany(group => group.Courses)
            .Select(course => course.CourseName)
            .Order(StringComparer.Ordinal));

    private static bool IsPersistedContext(PlannerViewModel viewModel) =>
        viewModel.CurrentSemester?.SemesterName == "Persisted semester" &&
        viewModel.CurrentPlan?.PlanName == "Persisted plan";

    private static string? CurrentPlanName(PlannerDocument document) =>
        document.Plans.FirstOrDefault(plan => string.Equals(
            plan.PlanId,
            document.Settings.CurrentPlanId,
            StringComparison.Ordinal))?.PlanName;

    private static string OpenPlanNames(PlannerDocument document)
    {
        var openIds = document.Settings.OpenPlanIds.ToHashSet(StringComparer.Ordinal);
        return PlanNames(document.Plans.Where(plan => openIds.Contains(plan.PlanId)));
    }

    private sealed record PublicationOutcome(
        string? FirstFailureType,
        string SaveEvents,
        string? CurrentSemester,
        string? CurrentPlan,
        string? DurableCurrentPlan,
        string DurableOpenPlans,
        string InstalledOpenPlans,
        string RenderedOpenPlans,
        string InstalledGroupedCourses,
        string RenderedGroupedCourses,
        bool RetrySucceeded,
        bool RetryWasValid);

    private sealed record FilterPublicationOutcome(
        string? FirstFailureType,
        string InstalledSearchText,
        string RenderedSearchText,
        string InstalledGroupedCourses,
        string RenderedGroupedCourses,
        int GroupResetCount,
        int SearchPropertyChangedCount);

    private sealed record RollbackPublicationOutcome(
        bool ContainsSaveFailure,
        bool ContainsPublicationFailure,
        string? CurrentSemester,
        string? CurrentPlan,
        string InstalledOpenPlans,
        string RenderedOpenPlans,
        string InstalledGroupedCourses,
        string RenderedGroupedCourses,
        int LaterRollbackSubscriberCalls,
        bool SessionConsistencyUnknown);

    private sealed record WholeProjectionRollbackOutcome(
        bool ContainsSaveFailure,
        bool ContainsSemesterPublicationFailure,
        int SemesterResetCount,
        int LabelResetCount,
        int AllPlansResetCount,
        int OpenPlansResetCount,
        int LibraryCoursesResetCount,
        int LibraryGroupsResetCount,
        int CurrentContextPropertyCount,
        int LaterRollbackSubscriberCalls,
        string FinalContext,
        string FinalOpenPlans,
        string FinalLibraryCourses);

    private sealed record ClosePublicationOutcome(
        string? FailureType,
        string SaveEvents,
        string CurrentContext,
        string? DurableCurrentPlan,
        string DurableOpenPlans,
        string InstalledOpenPlans,
        string RenderedOpenPlans,
        string InstalledGroupedCourses,
        string RenderedGroupedCourses);

    private sealed record CourseEditPublicationOutcome(
        string? FailureType,
        string SaveEvents,
        string DurableNotes,
        string LiveNotes,
        string? SelectedCourseName,
        bool IsDetailOpen,
        int DetailPropertyChangedCount);

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string directory,
            DocumentSession session,
            PlannerViewModel viewModel,
            SelectionPlan targetPlan)
        {
            Directory = directory;
            Session = session;
            ViewModel = viewModel;
            TargetPlan = targetPlan;
            DurableDocument = JsonDefaults.Clone(session.Document);
        }

        private string Directory { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }
        public SelectionPlan TargetPlan { get; }
        public PlannerDocument DurableDocument { get; private set; }
        public List<string> SaveEvents { get; } = [];
        public bool FailPlanSave { get; set; }
        public bool FailCloseSave { get; set; }

        public static Fixture Create()
        {
            var document = SeedData.Create("Persisted semester", "Persisted plan");
            var persistedSemester = document.Semesters[0];
            document.CourseLibrary.Add(CreateCourse(
                persistedSemester.SemesterId,
                "Persisted course",
                colorIndex: 0));
            document.CourseLibrary.Add(CreateCourse(
                persistedSemester.SemesterId,
                "Alternate course",
                colorIndex: 1));

            var targetSemester = JsonDefaults.Clone(persistedSemester);
            targetSemester.SemesterId = "target-semester";
            targetSemester.SemesterName = "Target semester";
            targetSemester.DisplayOrder = 1;
            document.Semesters.Add(targetSemester);
            document.CourseLibrary.Add(CreateCourse(
                targetSemester.SemesterId,
                "Target course",
                colorIndex: 2));
            var targetPlan = new SelectionPlan
            {
                SemesterId = targetSemester.SemesterId,
                PlanName = "Target plan",
                DisplayOrder = 1
            };
            document.Plans.Add(targetPlan);
            DocumentConsistencyService.Ensure(document);

            Fixture? fixture = null;
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"course-planner-publication-exception-{Guid.NewGuid():N}");
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(
                repository,
                loadDocument: () => document,
                saveDocument: (candidate, eventName) =>
                {
                    fixture?.SaveEvents.Add(eventName);
                    if (fixture?.FailPlanSave == true && eventName == "planner.plan")
                    {
                        throw new IOException("Injected planner.plan save failure.");
                    }
                    if (fixture?.FailCloseSave == true && eventName == "plan.close-tab")
                    {
                        throw new IOException("Injected plan.close-tab save failure.");
                    }
                    if (fixture is not null)
                        fixture.DurableDocument = JsonDefaults.Clone(candidate);
                });
            var viewModel = new PlannerViewModel(
                session,
                new LocalizationService(session));
            fixture = new Fixture(directory, session, viewModel, targetPlan);
            return fixture;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static CourseOffering CreateCourse(
            string semesterId,
            string name,
            int colorIndex)
        {
            var course = new CourseOffering
            {
                SemesterId = semesterId,
                CourseName = name,
                Teacher = "Teacher",
                Location = "Room",
                Credits = 1,
                Color = CourseColorService.Generate(colorIndex)
            };
            course.MeetingTimes.Add(new MeetingTime
            {
                Weekday = 1,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = "1-16"
            });
            CourseIdentityService.AssignOfferingId(course);
            return course;
        }
    }
}
