namespace CoursePlanner.Tests;

public sealed class ContentDialogCoordinatorArchitectureTests
{
    [Fact]
    public void EveryContentDialogUsesThePerDispatcherCoordinator()
    {
        var projectDirectory = SourcePath("CoursePlanner");
        var coordinatorPath = Path.Combine(projectDirectory, "Services", "ContentDialogCoordinator.cs");
        var coordinator = File.ReadAllText(coordinatorPath);

        Assert.Contains(
            "ConditionalWeakTable<DispatcherQueue, DispatcherDialogQueue>",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains("Queue<DialogRequest>", coordinator, StringComparison.Ordinal);
        Assert.Contains("Dictionary<XamlRoot, RootLifetime>", coordinator, StringComparison.Ordinal);
        Assert.Contains("finally", coordinator, StringComparison.Ordinal);
        Assert.Contains("ContentDialogResult.None", coordinator, StringComparison.Ordinal);
        Assert.Contains("Unloaded +=", coordinator, StringComparison.Ordinal);

        foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                     .Where(path => IsProjectSourceFile(projectDirectory, path)))
        {
            if (string.Equals(path, coordinatorPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var source = File.ReadAllText(path);
            Assert.DoesNotMatch(
                @"(?<!ContentDialogCoordinator)\.ShowAsync\s*\(",
                source);
        }
    }

    [Fact]
    public void CoordinatorRejectsSameDispatcherReentrancyAndKeepsRootCancellationScoped()
    {
        var coordinator = File.ReadAllText(
            Path.Combine(SourcePath("CoursePlanner"), "Services", "ContentDialogCoordinator.cs"));

        Assert.Contains("ActiveQueue", coordinator, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(ActiveQueue.Value, queue)", coordinator, StringComparison.Ordinal);
        Assert.Contains("TryRegisterDialog", coordinator, StringComparison.Ordinal);
        Assert.Contains("CancelRoot", coordinator, StringComparison.Ordinal);
        Assert.Contains("retainedRequests.Enqueue(request)", coordinator, StringComparison.Ordinal);
        Assert.Contains("ContentDialogResult.None", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentDialogCallbacksCannotAwaitAnotherCoordinatedDialog()
    {
        var projectDirectory = SourcePath("CoursePlanner");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
                .Where(path => IsProjectSourceFile(projectDirectory, path))
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith("ContentDialogCoordinator.cs", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        Assert.DoesNotContain("PrimaryButtonClick=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryButtonClick=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseButtonClick=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialogButtonClickEventArgs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialogClosingEventArgs", source, StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);

    private static bool IsProjectSourceFile(string projectDirectory, string path)
    {
        var segments = Path.GetRelativePath(projectDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
