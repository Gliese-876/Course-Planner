using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using CoursePlanner.Persistence;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class RuntimeOperationExceptionPolicyTests
{
    [Fact]
    public void StorageAndDatabaseFailuresAreRecoverableAtTheUiBoundary()
    {
        Exception[] recoverable =
        [
            new IOException("disk write failed"),
            new InvalidDataException("stored document is unreadable"),
            new UnauthorizedAccessException("storage access denied"),
            new TestDbException("database is locked"),
            CreateRepositoryValidationFailure(),
            new RepositoryRecoveryException(
                "repository recovery could not complete",
                @"C:\recovery-artifacts",
                new InvalidOperationException("wrapped details are not classified")),
            ComFailure(0x80070002), // ERROR_FILE_NOT_FOUND
            ComFailure(0x80070003), // ERROR_PATH_NOT_FOUND
            ComFailure(0x80070005), // ERROR_ACCESS_DENIED
            ComFailure(0x80070020), // ERROR_SHARING_VIOLATION
            ComFailure(0x80070070)  // ERROR_DISK_FULL
        ];

        Assert.All(
            recoverable,
            exception => Assert.True(RuntimeOperationExceptionPolicy.IsRecoverable(exception)));
    }

    [Fact]
    public void ProgrammingAndFatalFailuresAreNeverMasked()
    {
        Exception[] nonRecoverable =
        [
            new InvalidOperationException("invalid state"),
            new NullReferenceException("bug"),
            new ArgumentException("bug"),
            new MissingMethodException("mixed binaries"),
            new OutOfMemoryException("fatal"),
            new StackOverflowException("fatal"),
            new AccessViolationException("fatal"),
            new COMException("unknown WinRT failure", unchecked((int)0x80004005))
        ];

        Assert.All(
            nonRecoverable,
            exception => Assert.False(RuntimeOperationExceptionPolicy.IsRecoverable(exception)));
        Assert.All(
            nonRecoverable.Skip(4).Take(3),
            exception => Assert.True(RuntimeOperationExceptionPolicy.IsFatal(exception)));
    }

    [Fact]
    public void ARecoverableInnerExceptionDoesNotHideAnUnknownOuterFailure()
    {
        var exception = new Exception("unknown operation failure", new IOException("inner"));

        Assert.False(RuntimeOperationExceptionPolicy.IsRecoverable(exception));
    }

    [Fact]
    public void RestoreCompensationFailureIsNotRecoverableAtTheUiBoundary()
    {
        var exception = new DocumentRestoreCompensationException(
            new IOException("restore failed"),
            new InvalidOperationException("refresh projection is broken"));

        Assert.False(RuntimeOperationExceptionPolicy.IsRecoverable(exception));
        Assert.IsNotAssignableFrom<IOException>(exception);
    }

    [Fact]
    public void FatalFailuresRemainFatalWhenWrappedOrAggregated()
    {
        Exception[] wrappedFatalFailures =
        [
            new Exception("outer", new OutOfMemoryException("fatal")),
            new AggregateException(
                new IOException("ordinary operation failure"),
                new AccessViolationException("fatal")),
            new AggregateException(
                new Exception("nested", new BadImageFormatException("fatal")))
        ];

        Assert.All(
            wrappedFatalFailures,
            exception => Assert.True(RuntimeOperationExceptionPolicy.IsFatal(exception)));
    }

    private static COMException ComFailure(uint hresult) =>
        new("storage failure", unchecked((int)hresult));

    private static RepositoryStateValidationException CreateRepositoryValidationFailure() =>
        (RepositoryStateValidationException)Activator.CreateInstance(
            typeof(RepositoryStateValidationException),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new[] { "DocumentCapacityExceeded" }, false],
            culture: null)!;

    private sealed class TestDbException(string message) : DbException(message);
}
