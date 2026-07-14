using CoursePlanner.Services;
using System.Runtime.InteropServices;

namespace CoursePlanner.Tests;

public sealed class SingleInstanceRedirectPolicyTests
{
    [Fact]
    public void RedirectWaitIsFiniteAndRecognizesTheOfficialComTimeoutResult()
    {
        Assert.InRange(
            SingleInstanceRedirectPolicy.TimeoutMilliseconds,
            1_000u,
            30_000u);
        Assert.True(SingleInstanceRedirectPolicy.IsTimeout(unchecked((int)0x80010115)));
        Assert.False(SingleInstanceRedirectPolicy.IsTimeout(0));
        Assert.False(SingleInstanceRedirectPolicy.IsTimeout(unchecked((int)0x80004005)));
    }

    [Theory]
    [MemberData(nameof(OperationalFailures))]
    public void OnlyExplicitRedirectOperationalFailuresAreCaught(Exception exception) =>
        Assert.True(SingleInstanceRedirectPolicy.IsOperationalFailure(exception));

    [Theory]
    [MemberData(nameof(ProgrammingAndFatalFailures))]
    public void ProgrammingAndFatalFailuresStillPropagate(Exception exception) =>
        Assert.False(SingleInstanceRedirectPolicy.IsOperationalFailure(exception));

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public void PrimaryProcessExitRacesAreExpectedForegroundFailures(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(SingleInstanceRedirectPolicy.IsForegroundFailure(exception));
    }

    [Theory]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(OutOfMemoryException))]
    public void UnrelatedForegroundFailuresStillPropagate(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(SingleInstanceRedirectPolicy.IsForegroundFailure(exception));
    }

    public static TheoryData<Exception> OperationalFailures => new()
    {
        new COMException(),
        new IOException(),
        new UnauthorizedAccessException()
    };

    public static TheoryData<Exception> ProgrammingAndFatalFailures => new()
    {
        new NullReferenceException(),
        new InvalidOperationException(),
        new ArgumentException(),
        new OutOfMemoryException(),
        new AccessViolationException(),
        new BadImageFormatException()
    };
}
