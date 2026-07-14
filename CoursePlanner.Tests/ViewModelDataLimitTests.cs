using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class ViewModelDataLimitTests
{
    [Fact]
    public void OversizedLabelIsRejectedBeforeUndoOrPersistence()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.SelectedLabel = null;
        var beforeCount = fixture.Session.Document.Labels.Count;
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var result = fixture.Settings.UpsertLabel(
            new string('x', PlannerDataLimits.MaxTextFieldLength + 1),
            LabelKind.Ordinary);

        Assert.Contains(result.Errors, issue => issue.Code == "LabelNameTooLong");
        Assert.Equal(beforeCount, fixture.Session.Document.Labels.Count);
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void SemesterCreationTryApiRejectsCatalogLimitWithoutSideEffects()
    {
        using var fixture = Fixture.Create();
        while (fixture.Session.Document.Semesters.Count < PlannerDataLimits.MaxSemesters)
        {
            var index = fixture.Session.Document.Semesters.Count;
            fixture.Session.Document.Semesters.Add(new Semester
            {
                SemesterId = $"semester-limit-{index}",
                SemesterName = $"Semester {index}"
            });
        }
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var created = fixture.Settings.TryAddSemester(out var semester, out var validation);

        Assert.False(created);
        Assert.Null(semester);
        Assert.Contains(validation.Errors, issue => issue.Code == "SemesterCatalogMaximum");
        Assert.Equal(PlannerDataLimits.MaxSemesters, fixture.Session.Document.Semesters.Count);
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void SemesterCreationAtExactTextCapacityDoesNotChargeCurrentContextReferences()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var currentPlanIdLength = document.Settings.CurrentPlanId?.Length ?? 0;
        Assert.True(currentPlanIdLength > 0);
        var template = new Semester
        {
            SemesterId = "semester-" + new string('x', 32),
            SemesterName = fixture.Settings.T["NewSemester"],
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            WeekStartDay = WeekStartDay.Monday,
            WeekCount = 16,
            DisplayOrder = document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        template.EndDate = SemesterRules.CalculateEndDate(
            template.StartDate,
            template.WeekCount,
            template.WeekStartDay);
        var exactDelta = PlannerDocumentTextCapacity.Count(template);
        var currentText = PlannerDocumentTextCapacity.Count(document);
        var paddingLength = PlannerDataLimits.MaxAggregateTextCharacters - currentText - exactDelta;
        Assert.True(paddingLength > currentPlanIdLength);
        document.CourseLibrary[0].Notes += new string('x', checked((int)paddingLength));
        Assert.Equal(
            PlannerDataLimits.MaxAggregateTextCharacters,
            PlannerDocumentTextCapacity.Count(document) + exactDelta);

        Assert.True(fixture.Settings.TryAddSemester(out var created, out var validation),
            string.Join(", ", validation.Errors.Select(error => error.Code)));

        Assert.NotNull(created);
        Assert.Equal(PlannerDataLimits.MaxAggregateTextCharacters, PlannerDocumentTextCapacity.Count(document));
        Assert.Null(document.Settings.CurrentPlanId);
    }

    [Fact]
    public void PlanCreateAndCopyTryApisRejectCatalogLimitWithoutSideEffects()
    {
        using var fixture = Fixture.Create();
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        while (fixture.Session.Document.Plans.Count < PlannerDataLimits.MaxPlans)
        {
            var index = fixture.Session.Document.Plans.Count;
            fixture.Session.Document.Plans.Add(new SelectionPlan
            {
                PlanId = $"plan-limit-{index}",
                SemesterId = semesterId,
                PlanName = $"Plan {index}"
            });
        }
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var created = fixture.ViewModel.TryCreatePlan(null, out var plan, out var createValidation);
        var copied = fixture.ViewModel.TryCopyCurrentPlan(out var copy, out var copyValidation);

        Assert.False(created);
        Assert.False(copied);
        Assert.Null(plan);
        Assert.Null(copy);
        Assert.Contains(createValidation.Errors, issue => issue.Code == "PlanCatalogMaximum");
        Assert.Contains(copyValidation.Errors, issue => issue.Code == "PlanCatalogMaximum");
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void FullPlanCatalogRejectsLastTabReplacementBeforeClosingTheOnlyTab()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        foreach (var extraPlan in fixture.ViewModel.OpenPlans.Skip(1).ToArray())
            fixture.ViewModel.ClosePlanTab(extraPlan, persist: false);
        var onlyOpenPlan = Assert.Single(fixture.ViewModel.OpenPlans);
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        while (fixture.Session.Document.Plans.Count < PlannerDataLimits.MaxPlans)
        {
            var index = fixture.Session.Document.Plans.Count;
            fixture.Session.Document.Plans.Add(new SelectionPlan
            {
                PlanId = $"replacement-limit-{index}",
                SemesterId = semesterId,
                PlanName = $"Replacement limit {index}"
            });
        }
        var beforeOpenIds = fixture.Session.Document.Settings.OpenPlanIds.ToArray();
        var beforePlanCount = fixture.Session.Document.Plans.Count;
        var beforeCurrentPlan = fixture.ViewModel.CurrentPlan;

        var validation = fixture.ViewModel.ValidateLastPlanTabReplacement(onlyOpenPlan);

        Assert.Contains(validation.Errors, issue => issue.Code == "PlanCatalogMaximum");
        Assert.Same(beforeCurrentPlan, fixture.ViewModel.CurrentPlan);
        Assert.Equal([onlyOpenPlan], fixture.ViewModel.OpenPlans);
        Assert.Equal(beforeOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(beforePlanCount, fixture.Session.Document.Plans.Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void LastPlanTabReplacementUsesOneUndoAndRestoresTheClosingPlanAsOpen()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        foreach (var extraPlan in fixture.ViewModel.OpenPlans.Skip(1).ToArray())
            fixture.ViewModel.ClosePlanTab(extraPlan, persist: false);
        var closingPlan = Assert.Single(fixture.ViewModel.OpenPlans);
        var undoCount = fixture.Session.UndoRedo.UndoCount;
        var changed = 0;
        fixture.Session.Changed += (_, _) => changed++;

        var replaced = fixture.ViewModel.TryReplaceLastPlanTab(
            closingPlan,
            out var replacement,
            out var validation);

        Assert.True(replaced, string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(replacement);
        Assert.Equal(undoCount + 1, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal(1, changed);
        Assert.Equal([replacement!.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == closingPlan.PlanId);

        Assert.True(fixture.Session.Undo());

        Assert.Equal([closingPlan.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        Assert.Equal(closingPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == replacement.PlanId);
    }

    [Fact]
    public void PristineLastTabReplacementDiscardsTheTransientPlanButUndoRestoresItOpen()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var transientPlan = fixture.ViewModel.CreatePlanFromTab();
        foreach (var plan in fixture.ViewModel.OpenPlans.Where(plan => plan.PlanId != transientPlan.PlanId).ToArray())
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        Assert.Equal([transientPlan.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        var undoCount = fixture.Session.UndoRedo.UndoCount;

        var replaced = fixture.ViewModel.TryReplaceLastPlanTab(
            transientPlan,
            out var replacement,
            out var validation);

        Assert.True(replaced, string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(replacement);
        Assert.Equal(undoCount + 1, fixture.Session.UndoRedo.UndoCount);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == transientPlan.PlanId);
        Assert.Equal([replacement!.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));

        Assert.True(fixture.Session.Undo());

        Assert.Equal([transientPlan.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        Assert.Equal(transientPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == replacement.PlanId);
    }

    [Fact]
    public void FailedLastPlanReplacementSaveRollsBackWithoutLeakingReplacementOrBaselineState()
    {
        var failReplacementSave = false;
        using var fixture = Fixture.Create(
            usePersistence: false,
            saveDocument: (_, eventName) =>
            {
                if (failReplacementSave && eventName == "plan.replace-last-tab")
                    throw new IOException("Injected replacement save failure.");
            });
        var transientPlan = fixture.ViewModel.CreatePlanFromTab();
        foreach (var plan in fixture.ViewModel.OpenPlans.Where(plan => plan.PlanId != transientPlan.PlanId).ToArray())
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        fixture.ViewModel.PersistPlanTabState();
        Assert.Equal([transientPlan.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        var planCount = fixture.Session.Document.Plans.Count;
        var undoCount = fixture.Session.UndoRedo.UndoCount;
        failReplacementSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.TryReplaceLastPlanTab(
            transientPlan,
            out _,
            out _));

        Assert.Equal(planCount, fixture.Session.Document.Plans.Count);
        Assert.Equal(undoCount, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal([transientPlan.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == transientPlan.PlanId);

        failReplacementSave = false;
        var restoredTransient = Assert.Single(fixture.ViewModel.OpenPlans);
        fixture.ViewModel.ClosePlanTab(restoredTransient);
        Assert.DoesNotContain(
            fixture.Session.Document.Plans,
            plan => plan.PlanId == transientPlan.PlanId);
    }

    [Fact]
    public void TabCreatedPlanKeepsDeleteOnCloseProvenanceAcrossUndoRedoCycles()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var created = fixture.ViewModel.CreatePlanFromTab();
        var planId = created.PlanId;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(fixture.Session.Undo());
            Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == planId);
            Assert.True(fixture.Session.Redo());
            Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == planId);
        }

        var restored = fixture.Session.Document.Plans.Single(plan => plan.PlanId == planId);
        fixture.ViewModel.ClosePlanTab(restored);

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == planId);
    }

    [Fact]
    public void LastTabReplacementKeepsBothProvenanceStatesAcrossUndoRedoCycles()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var original = fixture.ViewModel.CreatePlanFromTab();
        foreach (var plan in fixture.ViewModel.OpenPlans.Where(plan => plan.PlanId != original.PlanId).ToArray())
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        fixture.ViewModel.PersistPlanTabState();
        Assert.True(fixture.ViewModel.TryReplaceLastPlanTab(original, out var replacement, out var validation),
            string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(replacement);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(fixture.Session.Undo());
            Assert.Equal(original.PlanId, Assert.Single(fixture.ViewModel.OpenPlans).PlanId);
            Assert.True(fixture.Session.Redo());
            Assert.Equal(replacement!.PlanId, Assert.Single(fixture.ViewModel.OpenPlans).PlanId);
        }

        Assert.True(fixture.Session.Undo());
        var restoredOriginal = Assert.Single(fixture.ViewModel.OpenPlans);
        fixture.ViewModel.ClosePlanTab(restoredOriginal);

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == original.PlanId);
    }

    [Fact]
    public void ReorderingTabsDoesNotTurnAPristineTabCreatedPlanIntoCatalogData()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var created = fixture.ViewModel.CreatePlanFromTab();
        var reversedOrder = fixture.ViewModel.OpenPlans
            .Select(plan => plan.PlanId)
            .Reverse()
            .ToArray();

        fixture.ViewModel.PersistOpenPlanOrder(reversedOrder);
        var reordered = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(reordered);

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void CommittedCreateRetainsProvenanceWhenALaterChangedSubscriberThrows()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        SelectionPlan? created = null;
        EventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected changed subscriber failure.");
        fixture.Session.Changed += throwingSubscriber;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                fixture.ViewModel.TryCreatePlanFromTab(out created, out _));
        }
        finally
        {
            fixture.Session.Changed -= throwingSubscriber;
        }

        Assert.NotNull(created);
        var committed = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(committed);

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void CommittedReplacementRetainsProvenanceWhenALaterChangedSubscriberThrows()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var original = fixture.ViewModel.CreatePlanFromTab();
        foreach (var plan in fixture.ViewModel.OpenPlans.Where(plan => plan.PlanId != original.PlanId).ToArray())
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        fixture.ViewModel.PersistPlanTabState();
        SelectionPlan? replacement = null;
        EventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected changed subscriber failure.");
        fixture.Session.Changed += throwingSubscriber;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                fixture.ViewModel.TryReplaceLastPlanTab(original, out replacement, out _));
        }
        finally
        {
            fixture.Session.Changed -= throwingSubscriber;
        }

        Assert.NotNull(replacement);
        var committed = Assert.Single(fixture.ViewModel.OpenPlans);
        Assert.Equal(replacement.PlanId, committed.PlanId);
        fixture.ViewModel.ClosePlanTab(committed);

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == replacement.PlanId);
    }

    [Fact]
    public void FailedTabPlanCreationDoesNotLeakProvenanceToAnEquivalentOrdinaryPlan()
    {
        var failSave = false;
        using var fixture = Fixture.Create(
            usePersistence: false,
            saveDocument: (_, _) =>
            {
                if (failSave)
                    throw new IOException("Injected create failure.");
            });
        failSave = true;
        SelectionPlan? failedPlan = null;

        Assert.Throws<IOException>(() =>
            fixture.ViewModel.TryCreatePlanFromTab(out failedPlan, out _));

        Assert.NotNull(failedPlan);
        failSave = false;
        fixture.Session.Document.Plans.Add(failedPlan);
        fixture.Session.Document.Settings.OpenPlanIds.Add(failedPlan.PlanId);
        fixture.ViewModel.ReloadFromDocument();
        fixture.ViewModel.ClosePlanTab(failedPlan, persist: false);

        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == failedPlan.PlanId);
    }

    [Fact]
    public void FailedRenameDoesNotPermanentlyLatchATabCreatedPlanAsModified()
    {
        var failSave = false;
        using var fixture = Fixture.Create(
            usePersistence: false,
            saveDocument: (_, eventName) =>
            {
                if (failSave && eventName == "plan.rename")
                    throw new IOException("Injected rename failure.");
            });
        var created = fixture.ViewModel.CreatePlanFromTab();
        failSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.RenamePlan(created, "Rejected rename"));

        failSave = false;
        var restored = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(restored);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void FailedNotifyFalseRegistrationReorderDoesNotLatchRolledBackPlanChanges()
    {
        var failSave = false;
        using var fixture = Fixture.Create(
            usePersistence: false,
            saveDocument: (_, eventName) =>
            {
                if (failSave && eventName == "plan.reorder-registration")
                    throw new IOException("Injected registration reorder failure.");
            });
        var created = fixture.ViewModel.CreatePlanFromTab();
        var originalOrder = AddRegistrationSnapshots(fixture, created);
        failSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.PersistRegistrationOrder(
            created.PlanId,
            originalOrder.Reverse().ToArray(),
            notify: false));

        failSave = false;
        var restored = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(restored);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void SuccessfulNotifyFalseRegistrationReorderLatchesMeaningfulPlanChanges()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var created = fixture.ViewModel.CreatePlanFromTab();
        var originalOrder = AddRegistrationSnapshots(fixture, created);

        Assert.True(fixture.ViewModel.PersistRegistrationOrder(
            created.PlanId,
            originalOrder.Reverse().ToArray(),
            notify: false));
        Assert.True(fixture.ViewModel.PersistRegistrationOrder(
            created.PlanId,
            originalOrder,
            notify: false));
        var livePlan = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(livePlan);

        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void EquivalentDocumentReplacementClearsHistoryAndTabCreationProvenance()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var created = fixture.ViewModel.CreatePlanFromTab();
        var replacement = JsonDefaults.Clone(fixture.Session.Document);

        fixture.Session.ReplaceDocument(replacement, "equivalent.replace");

        Assert.Equal(0, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal(0, fixture.Session.UndoRedo.RedoCount);
        var rebound = fixture.Session.Document.Plans.Single(plan => plan.PlanId == created.PlanId);
        fixture.ViewModel.ClosePlanTab(rebound);
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void SuccessfulExternalPlanDeletionEndsTransientProvenanceForAnEquivalentResurrection()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var created = fixture.ViewModel.CreatePlanFromTab();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans.Remove(created);
        fixture.Session.Document.Settings.OpenPlanIds.Remove(created.PlanId);
        fixture.Session.Save("external.plan-delete");

        fixture.Session.Document.Plans.Add(created);
        fixture.Session.Document.Settings.OpenPlanIds.Add(created.PlanId);
        fixture.ViewModel.ReloadFromDocument();
        fixture.ViewModel.ClosePlanTab(created, persist: false);

        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void RepeatedCaptureDeduplicatesEquivalentPartiallyPrunedTransientState()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var removed = fixture.ViewModel.CreatePlanFromTab();
        var retained = fixture.ViewModel.CreatePlanFromTab();
        fixture.Session.Document.Plans.Remove(removed);
        fixture.Session.Document.Settings.OpenPlanIds.Remove(removed.PlanId);

        Assert.True(fixture.Session.UndoRedo.Capture(fixture.Session.Document));
        var undoCount = fixture.Session.UndoRedo.UndoCount;
        var historyBytes = fixture.Session.UndoRedo.HistoryBytes;
        Assert.False(fixture.Session.UndoRedo.Capture(fixture.Session.Document));

        Assert.Equal(undoCount, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal(historyBytes, fixture.Session.UndoRedo.HistoryBytes);
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == retained.PlanId);
    }

    [Fact]
    public void PartialExternalSemesterDeletionCanonicalizesRemainingTransientState()
    {
        using var fixture = Fixture.Create();
        var deletedSemester = fixture.ViewModel.CurrentSemester!;
        var deletedTransient = fixture.ViewModel.CreatePlanFromTab();
        Assert.True(fixture.Settings.TryAddSemester(out var remainingSemester, out var validation),
            string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(remainingSemester);
        var remainingAnchor = new SelectionPlan
        {
            SemesterId = remainingSemester.SemesterId,
            PlanName = "Remaining semester anchor",
            DisplayOrder = fixture.Session.Document.Plans.Count
        };
        fixture.Session.Document.Plans.Add(remainingAnchor);
        Assert.True(fixture.ViewModel.TryOpenPlan(remainingAnchor, out var openValidation),
            string.Join(", ", openValidation.Errors.Select(error => error.Code)));
        Assert.Equal(remainingSemester.SemesterId, fixture.ViewModel.CurrentSemester?.SemesterId);
        var remainingTransient = fixture.ViewModel.CreatePlanFromTab();
        Assert.Equal(remainingSemester.SemesterId, remainingTransient.SemesterId);
        fixture.Settings.SelectedSemester = deletedSemester;

        Assert.True(fixture.Settings.DeleteSelectedSemester());

        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == deletedTransient.PlanId);
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == remainingTransient.PlanId);
        Assert.True(fixture.Session.UndoRedo.Capture(fixture.Session.Document));
        Assert.False(fixture.Session.UndoRedo.Capture(fixture.Session.Document));

        fixture.Session.Save("post-semester-delete.no-op");

        Assert.True(fixture.Session.UndoRedo.Capture(fixture.Session.Document));
        Assert.False(fixture.Session.UndoRedo.Capture(fixture.Session.Document));
    }

    [Fact]
    public void RestartTreatsPersistedPristineTabPlanAsDurableCatalogData()
    {
        using var fixture = Fixture.Create();
        var created = fixture.ViewModel.CreatePlanFromTab();
        var secondRepository = new SqliteAppRepository(fixture.Directory);
        var secondSession = new DocumentSession(secondRepository);
        var secondLocalization = new LocalizationService(secondSession);
        var secondViewModel = new PlannerViewModel(secondSession, secondLocalization);
        var reloaded = secondSession.Document.Plans.Single(plan => plan.PlanId == created.PlanId);

        secondViewModel.ClosePlanTab(reloaded);

        Assert.Contains(secondSession.Document.Plans, plan => plan.PlanId == created.PlanId);
        Assert.Contains(secondRepository.LoadOrCreate().Plans, plan => plan.PlanId == created.PlanId);
    }

    [Fact]
    public void LastPlanTabReplacementTextCapacityUsesTheExactNetState()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        foreach (var extraPlan in fixture.ViewModel.OpenPlans.Skip(1).ToArray())
            fixture.ViewModel.ClosePlanTab(extraPlan, persist: false);
        var closingPlan = Assert.Single(fixture.ViewModel.OpenPlans);
        closingPlan.PlanName = "Capacity boundary closing plan";
        fixture.Session.Document.Plans.RemoveAll(plan => plan.PlanId != closingPlan.PlanId);

        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var replacementName = fixture.ViewModel.T["NewPlan"];
        var replacementPlanText = 32 + semesterId.Length + replacementName.Length;
        var replacementNetDelta = replacementPlanText + 32 - closingPlan.PlanId.Length;
        var currentText = PlannerDocumentTextCapacity.Count(fixture.Session.Document);
        var paddingLength = PlannerDataLimits.MaxAggregateTextCharacters - currentText - replacementNetDelta;
        Assert.True(paddingLength > 0);
        fixture.Session.Document.CourseLibrary[0].Notes += new string('x', checked((int)paddingLength));
        Assert.Equal(
            PlannerDataLimits.MaxAggregateTextCharacters,
            PlannerDocumentTextCapacity.Count(fixture.Session.Document) + replacementNetDelta);

        var validation = fixture.ViewModel.ValidateLastPlanTabReplacement(closingPlan);

        Assert.True(validation.IsValid, string.Join(", ", validation.Errors.Select(error => error.Code)));
    }

    [Fact]
    public void OpeningCreatingAndCopyingAtPlanTabLimitAreRejectedAtomically()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        FillOpenPlanLimit(fixture);
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var closedPlan = new SelectionPlan
        {
            PlanId = "closed-plan-beyond-tab-limit",
            SemesterId = semesterId,
            PlanName = "Closed plan"
        };
        fixture.Session.Document.Plans.Add(closedPlan);
        var beforeCurrentPlan = fixture.ViewModel.CurrentPlan;
        var beforeCurrentSemester = fixture.ViewModel.CurrentSemester;
        var beforeOpenIds = fixture.Session.Document.Settings.OpenPlanIds.ToArray();
        var beforeOpenPlans = fixture.ViewModel.OpenPlans.ToArray();
        var beforePlanCount = fixture.Session.Document.Plans.Count;
        var changed = 0;
        fixture.Session.Changed += (_, _) => changed++;

        var opened = fixture.ViewModel.TryOpenPlan(closedPlan, out var openValidation);
        var created = fixture.ViewModel.TryCreatePlan("Rejected create", out var plan, out var createValidation);
        var copied = fixture.ViewModel.TryCopyCurrentPlan(out var copy, out var copyValidation);
        var copiedClosed = fixture.ViewModel.TryCopyPlan(
            closedPlan,
            out var closedCopy,
            out var closedCopyValidation);

        Assert.False(opened);
        Assert.False(created);
        Assert.False(copied);
        Assert.False(copiedClosed);
        Assert.Null(plan);
        Assert.Null(copy);
        Assert.Null(closedCopy);
        Assert.All(
            new[] { openValidation, createValidation, copyValidation, closedCopyValidation },
            validation => Assert.Contains(validation.Errors, issue => issue.Code == "OpenPlanTabsMaximum"));
        Assert.Same(beforeCurrentPlan, fixture.ViewModel.CurrentPlan);
        Assert.Same(beforeCurrentSemester, fixture.ViewModel.CurrentSemester);
        Assert.Equal(beforeOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(beforeOpenPlans, fixture.ViewModel.OpenPlans);
        Assert.Equal(beforePlanCount, fixture.Session.Document.Plans.Count);
        Assert.Equal(0, changed);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void CopyingAClosedPlanUsesOnlyTheNewTabsCapacitySlot()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var closedSource = new SelectionPlan
        {
            PlanId = "closed-copy-source",
            SemesterId = semesterId,
            PlanName = "Closed copy source"
        };
        document.Plans.Add(closedSource);
        while (document.Settings.OpenPlanIds.Count < PlanTabLimits.MaximumOpenPlans - 1)
        {
            var index = document.Settings.OpenPlanIds.Count;
            var plan = new SelectionPlan
            {
                PlanId = $"copy-capacity-open-{index}",
                SemesterId = semesterId,
                PlanName = $"Copy capacity open {index}"
            };
            document.Plans.Add(plan);
            document.Settings.OpenPlanIds.Add(plan.PlanId);
        }
        fixture.ViewModel.ReloadFromDocument();

        var copied = fixture.ViewModel.TryCopyPlan(closedSource, out var copy, out var validation);

        Assert.True(copied, string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(copy);
        Assert.Equal(PlanTabLimits.MaximumOpenPlans, fixture.ViewModel.OpenPlans.Count);
        Assert.DoesNotContain(closedSource.PlanId, document.Settings.OpenPlanIds);
        Assert.Contains(copy!.PlanId, document.Settings.OpenPlanIds);
        Assert.Same(copy, fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void ClosedPlanManagementDoesNotConsumeATabOrChangeTheCurrentPlanAtTheTabLimit()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        FillOpenPlanLimit(fixture);
        var document = fixture.Session.Document;
        var currentPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var beforeOpenIds = document.Settings.OpenPlanIds.ToArray();
        var closedTarget = JsonDefaults.Clone(document.Plans[0]);
        closedTarget.PlanId = "closed-management-target";
        closedTarget.PlanName = "Closed management target";
        document.Plans.Add(closedTarget);

        var clearValidation = fixture.ViewModel.ClearPlan(closedTarget);

        Assert.True(clearValidation.IsValid);
        Assert.Empty(closedTarget.Snapshots);
        Assert.Equal(beforeOpenIds, document.Settings.OpenPlanIds);
        Assert.Equal(currentPlanId, fixture.ViewModel.CurrentPlan?.PlanId);

        var deleteValidation = fixture.ViewModel.DeletePlan(closedTarget);

        Assert.True(deleteValidation.IsValid);
        Assert.DoesNotContain(document.Plans, plan => plan.PlanId == closedTarget.PlanId);
        Assert.Equal(beforeOpenIds, document.Settings.OpenPlanIds);
        Assert.Equal(currentPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
    }

    [Fact]
    public void CopyingAClosedPlanFromAnotherSemesterSwitchesOnlyToTheCopyAndIsUndoable()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var originalPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var originalSemesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var originalOpenIds = document.Settings.OpenPlanIds.ToArray();
        var otherSemester = new Semester
        {
            SemesterId = "copy-other-semester",
            SemesterName = "Copy other semester",
            DisplayOrder = document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        var source = new SelectionPlan
        {
            PlanId = "closed-cross-semester-copy-source",
            SemesterId = otherSemester.SemesterId,
            PlanName = "Cross-semester source",
            DisplayOrder = document.Plans.Count
        };
        document.Semesters.Add(otherSemester);
        document.Plans.Add(source);
        fixture.ViewModel.ReloadFromDocument();
        var expectedCopy = JsonDefaults.Clone(source);
        expectedCopy.PlanId = new string('c', 32);
        expectedCopy.PlanName = string.Format(
            fixture.ViewModel.T["CopiedPlanNameFormat"],
            source.PlanName);
        var copyTextDelta = PlannerDocumentTextCapacity.Count(expectedCopy) + expectedCopy.PlanId.Length;
        var paddingLength =
            PlannerDataLimits.MaxAggregateTextCharacters -
            PlannerDocumentTextCapacity.Count(document) -
            copyTextDelta;
        Assert.True(paddingLength > 0);
        document.CourseLibrary[0].Notes += new string('x', checked((int)paddingLength));

        var copied = fixture.ViewModel.TryCopyPlan(source, out var copy, out var validation);

        Assert.True(copied, string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.NotNull(copy);
        Assert.Equal(otherSemester.SemesterId, copy!.SemesterId);
        Assert.Equal(copy.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(otherSemester.SemesterId, fixture.ViewModel.CurrentSemester?.SemesterId);
        Assert.Equal(originalOpenIds.Append(copy.PlanId), document.Settings.OpenPlanIds);
        Assert.Equal(PlannerDataLimits.MaxAggregateTextCharacters, PlannerDocumentTextCapacity.Count(document));

        Assert.True(fixture.Session.Undo());

        Assert.Equal(originalOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(originalSemesterId, fixture.ViewModel.CurrentSemester?.SemesterId);
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == source.PlanId);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == copy.PlanId);
    }

    [Fact]
    public void ClosedPlanClearAndDeleteEachPreserveTheOpenContextAndUndoCleanly()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var originalPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var originalSemesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var originalOpenIds = document.Settings.OpenPlanIds.ToArray();
        var target = new SelectionPlan
        {
            PlanId = "closed-clear-delete-undo-target",
            SemesterId = originalSemesterId,
            PlanName = "Closed clear/delete target",
            Snapshots =
            {
                new PlanCourseSnapshot
                {
                    SnapshotId = "closed-clear-delete-snapshot",
                    CourseOfferingId = document.CourseLibrary[0].OfferingId
                }
            }
        };
        document.Plans.Add(target);

        Assert.True(fixture.ViewModel.ClearPlan(target).IsValid);
        Assert.Empty(fixture.Session.Document.Plans.Single(plan => plan.PlanId == target.PlanId).Snapshots);
        Assert.Equal(originalOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.True(fixture.Session.Undo());
        Assert.Single(fixture.Session.Document.Plans.Single(plan => plan.PlanId == target.PlanId).Snapshots);
        Assert.Equal(originalOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);

        var restoredTarget = fixture.Session.Document.Plans.Single(plan => plan.PlanId == target.PlanId);
        Assert.True(fixture.ViewModel.DeletePlan(restoredTarget).IsValid);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == target.PlanId);
        Assert.Equal(originalOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.True(fixture.Session.Undo());
        Assert.Contains(fixture.Session.Document.Plans, plan => plan.PlanId == target.PlanId);
        Assert.Equal(originalOpenIds, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
    }

    [Fact]
    public void TabCreatedPlanRemainsInTheCatalogAfterAddThenClearAndClose()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var plan = fixture.ViewModel.CreatePlanFromTab();
        var add = fixture.ViewModel.AddCourseToPlan(
            plan,
            fixture.Session.Document.CourseLibrary[0],
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);
        Assert.True(add.Added);
        Assert.True(fixture.ViewModel.ClearPlan(plan).IsValid);
        Assert.Empty(plan.Snapshots);

        fixture.ViewModel.ClosePlanTab(plan);

        Assert.Contains(fixture.Session.Document.Plans, candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, candidate => candidate.PlanId == plan.PlanId);
    }

    [Fact]
    public void TabCreatedPlanRemainsInTheCatalogAfterRenameBackAndClose()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var plan = fixture.ViewModel.CreatePlanFromTab();
        var originalName = plan.PlanName;
        Assert.True(fixture.ViewModel.RenamePlan(plan, "Temporary renamed plan").IsValid);
        Assert.True(fixture.ViewModel.RenamePlan(plan, originalName).IsValid);

        fixture.ViewModel.ClosePlanTab(plan);

        Assert.Contains(fixture.Session.Document.Plans, candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, candidate => candidate.PlanId == plan.PlanId);
    }

    [Fact]
    public void ExistingPlanCanStillBeSelectedAtPlanTabLimitWithoutOpeningAnotherTab()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        FillOpenPlanLimit(fixture);
        var target = fixture.ViewModel.OpenPlans.First(plan => !ReferenceEquals(plan, fixture.ViewModel.CurrentPlan));

        var opened = fixture.ViewModel.TryOpenPlan(target, out var validation);

        Assert.True(opened);
        Assert.True(validation.IsValid);
        Assert.Same(target, fixture.ViewModel.CurrentPlan);
        Assert.Equal(PlanTabLimits.MaximumOpenPlans, fixture.ViewModel.OpenPlans.Count);
        Assert.Equal(
            PlanTabLimits.MaximumOpenPlans,
            fixture.Session.Document.Settings.OpenPlanIds.Count);
    }

    [Fact]
    public void ZeroWeekLegacySemesterIsClampedWithoutCrashingAcrossSelectionPaths()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var originalPlan = fixture.ViewModel.CurrentPlan!;
        var originalSemester = fixture.ViewModel.CurrentSemester!;
        foreach (var extra in fixture.ViewModel.OpenPlans
                     .Where(plan => plan.PlanId != originalPlan.PlanId)
                     .ToArray())
        {
            fixture.ViewModel.ClosePlanTab(extra, persist: false);
        }

        var zeroWeekSemester = new Semester
        {
            SemesterId = "legacy-zero-week-semester",
            SemesterName = "Legacy zero-week semester",
            WeekCount = 0,
            DisplayOrder = document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        var zeroWeekPlan = new SelectionPlan
        {
            PlanId = "legacy-zero-week-plan",
            SemesterId = zeroWeekSemester.SemesterId,
            PlanName = "Legacy zero-week plan",
            DisplayOrder = document.Plans.Count
        };
        document.Semesters.Add(zeroWeekSemester);
        document.Plans.Add(zeroWeekPlan);
        fixture.ViewModel.ReloadFromDocument();
        fixture.ViewModel.CurrentWeek = 5;
        Assert.Equal(5, fixture.ViewModel.CurrentWeek);
        zeroWeekSemester.WeekCount = 0;

        Assert.True(
            fixture.ViewModel.TryOpenPlan(zeroWeekPlan, out var validation),
            string.Join(", ", validation.Errors.Select(error => error.Code)));
        Assert.Equal(1, fixture.ViewModel.CurrentWeek);

        fixture.ViewModel.CurrentSemester = originalSemester;
        fixture.ViewModel.CurrentWeek = 5;
        Assert.Equal(5, fixture.ViewModel.CurrentWeek);
        zeroWeekSemester.WeekCount = 0;
        fixture.ViewModel.CurrentSemester = zeroWeekSemester;
        Assert.Equal(1, fixture.ViewModel.CurrentWeek);

        fixture.ViewModel.CurrentPlan = originalPlan;
        Assert.Same(originalPlan, fixture.ViewModel.CurrentPlan);
        Assert.Same(originalSemester, fixture.ViewModel.CurrentSemester);
        fixture.ViewModel.CurrentWeek = 5;
        Assert.Equal(5, fixture.ViewModel.CurrentWeek);
        zeroWeekSemester.WeekCount = 0;
        fixture.ViewModel.ClosePlanTab(originalPlan, persist: false);
        Assert.Same(zeroWeekPlan, fixture.ViewModel.CurrentPlan);
        Assert.Equal(1, fixture.ViewModel.CurrentWeek);
    }

    [Fact]
    public void OpeningAClosedCrossSemesterPlanIsExactAtTheTextBoundaryAndRejectsOneMoreCharacterAtomically()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var otherSemester = new Semester
        {
            SemesterId = "open-capacity-other-semester",
            SemesterName = "Open capacity other semester",
            DisplayOrder = document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        var target = new SelectionPlan
        {
            PlanId = "closed-cross-semester-capacity-target",
            SemesterId = otherSemester.SemesterId,
            PlanName = "Closed cross-semester capacity target",
            DisplayOrder = document.Plans.Count
        };
        document.Semesters.Add(otherSemester);
        document.Plans.Add(target);
        fixture.ViewModel.ReloadFromDocument();
        var paddingLength =
            PlannerDataLimits.MaxAggregateTextCharacters -
            PlannerDocumentTextCapacity.Count(document) -
            target.PlanId.Length;
        Assert.True(paddingLength > 0);
        document.CourseLibrary[0].Notes += new string('x', checked((int)paddingLength));

        Assert.True(
            fixture.ViewModel.TryOpenPlan(target, out var exactValidation),
            string.Join(", ", exactValidation.Errors.Select(error => error.Code)));
        Assert.Equal(target.PlanId, document.Settings.CurrentPlanId);
        Assert.Equal(otherSemester.SemesterId, document.Settings.CurrentSemesterId);
        Assert.Equal(PlannerDataLimits.MaxAggregateTextCharacters, PlannerDocumentTextCapacity.Count(document));

        fixture.ViewModel.ClosePlanTab(target);
        document.CourseLibrary[0].Notes += "x";
        var beforeOpenIds = document.Settings.OpenPlanIds.ToArray();
        var beforeCurrentPlanId = document.Settings.CurrentPlanId;
        var beforeCurrentSemesterId = document.Settings.CurrentSemesterId;

        Assert.False(fixture.ViewModel.TryOpenPlan(target, out var overflowValidation));
        Assert.Contains(overflowValidation.Errors, error => error.Code == "AggregateTextMaximum");
        Assert.Equal(beforeOpenIds, document.Settings.OpenPlanIds);
        Assert.Equal(beforeCurrentPlanId, document.Settings.CurrentPlanId);
        Assert.Equal(beforeCurrentSemesterId, document.Settings.CurrentSemesterId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, plan => plan.PlanId == target.PlanId);
    }

    [Fact]
    public void DocumentConsistencyBoundsLegacyOpenPlanStateButKeepsThePlanCatalog()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var semesterId = document.Semesters[0].SemesterId;
        for (var index = document.Settings.OpenPlanIds.Count;
             index < PlanTabLimits.MaximumOpenPlans + 3;
             index++)
        {
            var plan = new SelectionPlan
            {
                PlanId = $"legacy-open-plan-{index}",
                SemesterId = semesterId,
                PlanName = $"Legacy {index}"
            };
            document.Plans.Add(plan);
            document.Settings.OpenPlanIds.Add(plan.PlanId);
        }
        var catalogCount = document.Plans.Count;

        DocumentConsistencyService.Ensure(document);

        Assert.Equal(PlanTabLimits.MaximumOpenPlans, document.Settings.OpenPlanIds.Count);
        Assert.Equal(catalogCount, document.Plans.Count);
        Assert.Contains(document.Settings.CurrentPlanId!, document.Settings.OpenPlanIds);
    }

    [Fact]
    public void PersistenceRejectsTooManyOpenPlanTabsBeforeReplacingStoredState()
    {
        using var fixture = Fixture.Create();
        var persistedPlanCount = fixture.Repository.LoadOrCreate().Plans.Count;
        var document = fixture.Session.Document;
        var semesterId = document.Semesters[0].SemesterId;
        while (document.Settings.OpenPlanIds.Count <= PlanTabLimits.MaximumOpenPlans)
        {
            var index = document.Settings.OpenPlanIds.Count;
            var plan = new SelectionPlan
            {
                PlanId = $"persisted-open-plan-overflow-{index}",
                SemesterId = semesterId,
                PlanName = $"Overflow {index}"
            };
            document.Plans.Add(plan);
            document.Settings.OpenPlanIds.Add(plan.PlanId);
        }

        var exception = Assert.Throws<RepositoryStateValidationException>(() =>
            fixture.Repository.Save(document, "rejected-open-plan-overflow"));

        Assert.Contains("Settings.OpenPlanIds.TooMany", exception.IssueCodes);
        Assert.Equal(persistedPlanCount, fixture.Repository.LoadOrCreate().Plans.Count);
    }

    [Fact]
    public void InvalidCourseTextIsRejectedBeforeIdentityAssignmentOrMutation()
    {
        using var fixture = Fixture.Create();
        var edit = fixture.ViewModel.BeginNewCourseEdit();
        edit.Course.CourseName = new string('x', PlannerDataLimits.MaxTextFieldLength + 1);
        var beforeCourses = fixture.Session.Document.CourseLibrary.Count;
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var validation = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.Contains(validation.Errors, issue => issue.Code == "CourseNameTooLong");
        Assert.Equal("", edit.Course.OfferingId);
        Assert.Same(edit, fixture.ViewModel.ActiveEdit);
        Assert.Equal(beforeCourses, fixture.Session.Document.CourseLibrary.Count);
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void NewCourseIsRejectedAtCourseCatalogLimit()
    {
        using var fixture = Fixture.Create();
        while (fixture.Session.Document.CourseLibrary.Count < PlannerDataLimits.MaxCourses)
            fixture.Session.Document.CourseLibrary.Add(new CourseOffering());
        var edit = fixture.ViewModel.BeginNewCourseEdit();
        edit.Course.CourseName = "Capacity boundary course";
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var validation = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.Contains(validation.Errors, issue => issue.Code == "CourseCatalogMaximum");
        Assert.Equal(PlannerDataLimits.MaxCourses, fixture.Session.Document.CourseLibrary.Count);
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void NewCourseIsRejectedWhenItsLabelWouldExceedGlobalReferenceLimit()
    {
        using var fixture = Fixture.Create();
        fixture.Session.Document.CourseLibrary[0].Labels =
            Enumerable.Repeat("existing", PlannerDataLimits.MaxTotalLabelReferences).ToList();
        var edit = fixture.ViewModel.BeginNewCourseEdit();
        edit.Course.CourseName = "Label boundary course";
        edit.Course.Labels = ["new-reference"];
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var validation = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.Contains(validation.Errors, issue => issue.Code == "TotalLabelReferencesMaximum");
        Assert.DoesNotContain(
            fixture.Session.Document.CourseLibrary,
            course => string.Equals(course.CourseName, edit.Course.CourseName, StringComparison.Ordinal));
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void PlanAddRejectsNetNewSnapshotAtPlanLimitWithoutSaveOrUndo()
    {
        using var fixture = Fixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        plan.Snapshots = Enumerable.Range(0, PlannerDataLimits.MaxSnapshotsPerPlan)
            .Select(index => new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-limit-{index}",
                CourseOfferingId = $"missing-course-{index}"
            })
            .ToList();
        var course = fixture.Session.Document.CourseLibrary.First(candidate =>
            plan.Snapshots.All(snapshot => snapshot.CourseOfferingId != candidate.OfferingId));
        var beforeEvents = fixture.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.AddCourseToPlan(
            plan,
            course,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.True(result.Cancelled);
        Assert.False(result.Added);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "PlanSnapshotsMaximum");
        Assert.Equal(PlannerDataLimits.MaxSnapshotsPerPlan, plan.Snapshots.Count);
        Assert.Equal(beforeEvents, fixture.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void BulkPlanAddAccountsForEarlierAddsBeforeCheckingGlobalSnapshotLimit()
    {
        using var fixture = Fixture.Create(usePersistence: false);
        var document = fixture.Session.Document;
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var firstTarget = fixture.ViewModel.CurrentPlan!;
        firstTarget.Snapshots.Clear();
        var secondTarget = new SelectionPlan
        {
            PlanId = "second-target",
            SemesterId = semesterId,
            PlanName = "Second target"
        };
        document.Plans.Add(secondTarget);

        var existingSnapshotCount = document.Plans.Sum(plan => plan.Snapshots.Count);
        var remaining = PlannerDataLimits.MaxTotalSnapshots - 1 - existingSnapshotCount;
        var fillerIndex = 0;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, PlannerDataLimits.MaxSnapshotsPerPlan);
            document.Plans.Add(new SelectionPlan
            {
                PlanId = $"f{fillerIndex}",
                SemesterId = semesterId,
                PlanName = $"F{fillerIndex}",
                Snapshots = Enumerable.Range(0, count)
                    .Select(_ => new PlanCourseSnapshot
                    {
                        SnapshotId = "",
                        CourseOfferingId = ""
                    })
                    .ToList()
            });
            fillerIndex++;
            remaining -= count;
        }
        var course = document.CourseLibrary[0];

        var result = fixture.ViewModel.AddCourseToPlans(
            [firstTarget, secondTarget],
            course,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Cancelled);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "TotalSnapshotsMaximum");
    }

    [Fact]
    public void UiCreationPathsUseNonThrowingValidationApis()
    {
        var root = FindRepositoryRoot();
        var sources = new[]
        {
            File.ReadAllText(Path.Combine(root, "CoursePlanner", "MainWindow.xaml.cs")),
            File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "PlannerPage.xaml.cs")),
            File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "PlansPage.xaml.cs")),
            File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "SemestersPage.xaml.cs"))
        };

        Assert.All(sources, source =>
        {
            Assert.DoesNotContain(".CreatePlan();", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".CreatePlanFromTab();", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".CopyCurrentPlan();", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".AddSemester();", source, StringComparison.Ordinal);
        });
        Assert.Contains("TryCreatePlanFromTab", sources[0], StringComparison.Ordinal);
        Assert.Contains("TryCreatePlan", sources[1], StringComparison.Ordinal);
        Assert.Contains("TryCopyPlan", sources[2], StringComparison.Ordinal);
        Assert.Contains("TryAddSemester", sources[3], StringComparison.Ordinal);
    }

    [Fact]
    public void PlanManagementCommandsOperateOnTheirTargetWithoutOpeningOrSelectingItFirst()
    {
        var root = FindRepositoryRoot();
        var plansPage = File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "PlansPage.xaml.cs"));
        var copy = MethodBody(plansPage, "private async void CopyPlan_Click", "private async void RenamePlan_Click");
        var clear = MethodBody(plansPage, "private async void ClearPlan_Click", "private async void DeletePlan_Click");
        var delete = MethodBody(plansPage, "private async void DeletePlan_Click", "private async Task RenamePlanAsync");

        Assert.Contains("TryCopyPlan(sourcePlan", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("TryOpenPlan", copy, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ClearPlan(plan)", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("TryOpenPlan", clear, StringComparison.Ordinal);
        Assert.True(
            clear.IndexOf("ConfirmAsync(", StringComparison.Ordinal) <
            clear.IndexOf("ViewModel.ClearPlan(plan)", StringComparison.Ordinal));
        Assert.Contains("ViewModel.DeletePlan(plan)", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("TryOpenPlan", delete, StringComparison.Ordinal);
        Assert.True(
            delete.IndexOf("ConfirmAsync(", StringComparison.Ordinal) <
            delete.IndexOf("ViewModel.DeletePlan(plan)", StringComparison.Ordinal));

        var mainWindow = File.ReadAllText(Path.Combine(root, "CoursePlanner", "MainWindow.xaml.cs"));
        var tabMenu = MethodBody(mainWindow, "private MenuFlyout CreatePlanTabMenu", "private static MenuFlyoutItem MenuItem");
        Assert.Contains("TryCopyPlan(plan", tabMenu, StringComparison.Ordinal);
        Assert.Contains("ClearPlan(plan)", tabMenu, StringComparison.Ordinal);
        Assert.Contains("DeletePlan(plan)", tabMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentPlan = plan", tabMenu, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanTabRenderingCachesMetricsAndDoesNotExpandEverySemesterSlotPerTitle()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "CoursePlanner", "MainWindow.xaml.cs"));

        Assert.Contains("_planTabMetrics.TryGetValue", shell, StringComparison.Ordinal);
        Assert.Contains("_planTabMetrics.Clear();", shell, StringComparison.Ordinal);
        Assert.Contains("TimetableConflictService.CountConflictSlots", shell, StringComparison.Ordinal);
        Assert.Contains("BuildPlanTabSignature(presentations)", shell, StringComparison.Ordinal);
        Assert.Contains("AppendSignatureValue(signature, presentation.Title)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("PlanConflictSlotCount", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("PlannerDomainService", shell, StringComparison.Ordinal);
        Assert.Contains("PlanTabLimits.MaximumOpenPlans", File.ReadAllText(
            Path.Combine(root, "CoursePlanner.Persistence", "PlannerDocumentPersistenceValidator.cs")),
            StringComparison.Ordinal);
    }

    private static string[] AddRegistrationSnapshots(Fixture fixture, SelectionPlan plan)
    {
        plan.Snapshots = fixture.Session.Document.CourseLibrary
            .Take(2)
            .Select(course => new PlanCourseSnapshot { CourseOfferingId = course.OfferingId })
            .ToList();
        DocumentConsistencyService.Ensure(fixture.Session.Document);
        return plan.Snapshots
            .OrderBy(snapshot => snapshot.RegistrationOrder)
            .Select(snapshot => snapshot.SnapshotId)
            .ToArray();
    }

    private static void FillOpenPlanLimit(Fixture fixture)
    {
        var document = fixture.Session.Document;
        var semesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        while (document.Settings.OpenPlanIds.Count < PlanTabLimits.MaximumOpenPlans)
        {
            var index = document.Settings.OpenPlanIds.Count;
            var plan = new SelectionPlan
            {
                PlanId = $"open-plan-limit-{index}",
                SemesterId = semesterId,
                PlanName = $"Open plan {index}"
            };
            document.Plans.Add(plan);
            document.Settings.OpenPlanIds.Add(plan.PlanId);
        }
        fixture.ViewModel.ReloadFromDocument();
    }

    private static string FindRepositoryRoot() => RepositoryPaths.Root;

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string directory,
            SqliteAppRepository repository,
            DocumentSession session,
            PlannerViewModel viewModel,
            SettingsViewModel settings)
        {
            Directory = directory;
            Repository = repository;
            Session = session;
            ViewModel = viewModel;
            Settings = settings;
        }

        public string Directory { get; }
        public SqliteAppRepository Repository { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }
        public SettingsViewModel Settings { get; }

        public static Fixture Create(
            bool usePersistence = true,
            Action<PlannerDocument, string>? saveDocument = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directory);
            var document = TestDocumentFactory.CreatePopulated();
            var session = saveDocument is not null
                ? new DocumentSession(
                    repository,
                    loadDocument: () => document,
                    saveDocument: saveDocument)
                : usePersistence
                ? new DocumentSession(repository)
                : new DocumentSession(
                    repository,
                    loadDocument: () => document,
                    saveDocument: (_, _) => { });
            if (usePersistence)
                session.ReplaceDocument(document, "test.seed");
            var localization = new LocalizationService(session);
            var viewModel = new PlannerViewModel(session, localization);
            var settings = new SettingsViewModel(session, localization, new TestThemeService());
            return new Fixture(directory, repository, session, viewModel, settings);
        }

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (System.IO.Directory.Exists(Directory))
                        System.IO.Directory.Delete(Directory, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed class TestThemeService : IThemeService
    {
        public ThemeMode RequestedTheme { get; private set; } = ThemeMode.FollowSystem;
        public ResolvedThemeMode ResolvedTheme => ResolveTheme(RequestedTheme);
        public bool IsHighContrast => false;
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) =>
            requestedTheme == ThemeMode.Dark ? ResolvedThemeMode.Dark : ResolvedThemeMode.Light;

        public void ApplyTheme(ThemeMode theme)
        {
            RequestedTheme = theme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(theme, ResolvedTheme, IsHighContrast));
        }

        public void RefreshTheme() =>
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(RequestedTheme, ResolvedTheme, IsHighContrast));
    }
}
