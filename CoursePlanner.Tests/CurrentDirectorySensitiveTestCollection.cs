namespace CoursePlanner.Tests;

[CollectionDefinition("Process current-directory mutation", DisableParallelization = true)]
public sealed class CurrentDirectorySensitiveTestCollection
{
    public const string Name = "Process current-directory mutation";
}
