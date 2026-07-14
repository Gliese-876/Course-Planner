namespace CoursePlanner.Tests;

[Collection(CurrentDirectorySensitiveTestCollection.Name)]
public sealed class RepositoryPathsTests
{
    [Fact]
    public void RepositoryResolutionDoesNotDependOnTheOutputOrProcessCurrentDirectory()
    {
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var expectedRoot = RepositoryPaths.FindRepositoryRoot();
        Assert.True(File.Exists(Path.Combine(expectedRoot, "CoursePlannerWorkspace.slnx")));
        var randomDirectory = Path.Combine(
            Path.GetTempPath(),
            $"course-planner-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(randomDirectory);

        try
        {
            foreach (var currentDirectory in new[]
                     {
                         Path.Combine(expectedRoot, "CoursePlanner.Tests"),
                         randomDirectory
                     })
            {
                Environment.CurrentDirectory = currentDirectory;
                Assert.Equal(expectedRoot, RepositoryPaths.FindRepositoryRoot());
                Assert.Equal(
                    Path.Combine(expectedRoot, "CoursePlanner", "CoursePlanner.csproj"),
                    RepositoryPaths.FromRoot("CoursePlanner", "CoursePlanner.csproj"));
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            Directory.Delete(randomDirectory, recursive: true);
        }
    }

    [Fact]
    public void RepositoryResolutionRejectsMissingAndAmbiguousMarkers()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"course-planner-root-probe-{Guid.NewGuid():N}");
        var outer = Path.Combine(temporaryRoot, "outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);

        try
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                RepositoryPaths.FindRepositoryRoot(inner));

            File.WriteAllText(Path.Combine(outer, "CoursePlannerWorkspace.slnx"), "");
            File.WriteAllText(Path.Combine(inner, "CoursePlannerWorkspace.slnx"), "");
            Assert.Throws<InvalidOperationException>(() =>
                RepositoryPaths.FindRepositoryRoot(inner));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void RepositoryPathsCannotEscapeTheDiscoveredRoot()
    {
        Assert.Throws<ArgumentException>(() => RepositoryPaths.FromRoot("..", "outside"));
    }
}
