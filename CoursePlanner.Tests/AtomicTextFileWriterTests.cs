using System.Text;
using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class AtomicTextFileWriterTests
{
    [Fact]
    public async Task SuccessfulWriteAtomicallyReplacesExistingFileAsUtf8WithoutBom()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-atomic-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(path, "old complete report");
            const string replacement = "{\"status\":\"完整✓\"}";

            await AtomicTextFileWriter.WriteAllTextAsync(path, replacement);

            Assert.Equal(replacement, await File.ReadAllTextAsync(path));
            Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulWriteReplacesTheWholeFileAndLeavesNoTemporaryArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-atomic-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(path, "old tail that must disappear");

            await AtomicTextFileWriter.WriteAllTextAsync(path, "新");

            Assert.Equal("新", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MaximumLengthDestinationNameDoesNotMakeTheTemporaryNameInvalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-atomic-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var fileName = new string('a', WindowsFileNameRules.MaxComponentLength - ".txt".Length) + ".txt";
            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, "old");

            await AtomicTextFileWriter.WriteAllTextAsync(path, "replacement");

            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledWritePreservesExistingFileAndPublishesNoTemporaryArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-atomic-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(path, "old complete report");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicTextFileWriter.WriteAllTextAsync(path, new string('新', 1_000_000), cancellation.Token));

            Assert.Equal("old complete report", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidUtf16PreservesExistingFileAndPublishesNoTemporaryArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-atomic-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(path, "old complete report");

            await Assert.ThrowsAsync<EncoderFallbackException>(() =>
                AtomicTextFileWriter.WriteAllTextAsync(path, "invalid \uD800 text"));

            Assert.Equal("old complete report", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
