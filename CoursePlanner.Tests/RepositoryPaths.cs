using System.Reflection;

namespace CoursePlanner.Tests;

internal static class RepositoryPaths
{
    private const string RepositoryMarker = "CoursePlannerWorkspace.slnx";
    private const string RepositoryRootMetadataKey = "CoursePlannerRepositoryRoot";
    private static readonly Lazy<string> CachedRoot = new(
        FindRepositoryRoot,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Root => CachedRoot.Value;

    public static string FromRoot(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Any(static part => part is null))
            throw new ArgumentException("Repository path segments cannot contain null values.", nameof(parts));

        var candidate = Path.GetFullPath(Path.Combine([Root, .. parts]));
        var relative = Path.GetRelativePath(Root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested path escapes the repository root.", nameof(parts));
        }

        return candidate;
    }

    internal static string FindRepositoryRoot()
    {
        var configuredRoots = typeof(RepositoryPaths).Assembly
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .Where(static attribute => attribute.Key == RepositoryRootMetadataKey)
            .Select(static attribute => attribute.Value)
            .ToArray();

        if (configuredRoots.Length != 1)
        {
            throw new InvalidOperationException(
                $"The test assembly must declare exactly one '{RepositoryRootMetadataKey}' metadata value.");
        }

        var configuredRootValue = configuredRoots[0];
        if (string.IsNullOrWhiteSpace(configuredRootValue))
        {
            throw new InvalidOperationException(
                $"The test assembly metadata '{RepositoryRootMetadataKey}' must not be empty.");
        }

        var configuredRoot = Path.GetFullPath(configuredRootValue);
        var discoveredRoot = FindRepositoryRoot(configuredRoot);
        if (!string.Equals(configuredRoot, discoveredRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configured repository root '{configuredRoot}' resolves to a different marker root '{discoveredRoot}'.");
        }

        return configuredRoot;
    }

    internal static string FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        var matches = new List<string>();
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, RepositoryMarker)))
                matches.Add(current.FullName);
            current = current.Parent;
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DirectoryNotFoundException(
                $"Could not locate '{RepositoryMarker}' above '{startDirectory}'."),
            _ => throw new InvalidOperationException(
                $"Repository root is ambiguous above '{startDirectory}': {string.Join(", ", matches)}")
        };
    }
}
