using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class JsonInputGuardTests
{
    [Fact]
    public void DuplicatePropertiesAreRejectedByTheStreamingGuard()
    {
        Assert.Throws<DuplicateJsonPropertyException>(() =>
            JsonInputGuard.Validate("{\"kind\":\"first\",\"kind\":\"second\"}"));
    }

    [Fact]
    public void ObjectsAndArraysHaveHardStructuralLimitsBeforeDomMaterialization()
    {
        var objectBuilder = new StringBuilder("{");
        for (var index = 0; index <= JsonInputGuard.MaximumPropertiesPerObject; index++)
        {
            if (index > 0)
                objectBuilder.Append(',');
            objectBuilder.Append('"').Append('p').Append(index).Append("\":0");
        }
        objectBuilder.Append('}');
        var oversizedArray = "[" +
                             string.Join(',', Enumerable.Repeat("0", JsonInputGuard.MaximumItemsPerArray + 1)) +
                             "]";

        Assert.Throws<JsonException>(() => JsonInputGuard.Validate(objectBuilder.ToString()));
        Assert.Throws<JsonException>(() => JsonInputGuard.Validate(oversizedArray));
    }

    [Fact]
    public void TotalTokenLimitRejectsWideNestedInputInBoundedTime()
    {
        const int innerItems = 1_000;
        var inner = "[" + string.Join(',', Enumerable.Repeat("0", innerItems)) + "]";
        var outerItems = JsonInputGuard.MaximumTokenCount / (innerItems + 2) + 1;
        Assert.InRange(outerItems, 1, JsonInputGuard.MaximumItemsPerArray);
        var json = "[" + string.Join(',', Enumerable.Repeat(inner, outerItems)) + "]";
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<JsonException>(() => JsonInputGuard.Validate(json));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Token-bomb rejection took {stopwatch.Elapsed}.");
    }
}
