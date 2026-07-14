using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Exchange;

namespace CoursePlanner.Tests;

public sealed class RegistrationOrderSerializationTests
{
    [Fact]
    public void SelectionPlanExportPreservesRegistrationOrderMetadata()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var plan = document.Plans[0];
        var requestedOrder = plan.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .Reverse()
            .ToList();
        Assert.True(RegistrationPriorityService.ApplyOrder(plan, requestedOrder));

        var json = ImportExportService.ExportSelectionPlanJson(document, plan);
        var package = JsonSerializer.Deserialize<SelectionPlanPackage>(json, JsonDefaults.Options);

        Assert.NotNull(package);
        Assert.Equal(
            requestedOrder,
            package!.Plan.Snapshots
                .OrderBy(snapshot => snapshot.RegistrationOrder)
                .Select(snapshot => snapshot.SnapshotId));
    }

    [Fact]
    public void RegistrationOrderJsonCloneKeepsSnapshotStorageOrderIndependent()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var plan = document.Plans[0];
        var snapshotStorageOrder = plan.Snapshots.Select(snapshot => snapshot.SnapshotId).ToList();
        var requestedOrder = snapshotStorageOrder.AsEnumerable().Reverse().ToList();
        RegistrationPriorityService.ApplyOrder(plan, requestedOrder);

        var clone = JsonDefaults.Clone(plan);

        Assert.Equal(snapshotStorageOrder, clone.Snapshots.Select(snapshot => snapshot.SnapshotId));
        Assert.Equal(
            requestedOrder,
            clone.Snapshots.OrderBy(snapshot => snapshot.RegistrationOrder).Select(snapshot => snapshot.SnapshotId));
    }
}
