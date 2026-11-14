using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class CommandGroupTests
{
    [Fact]
    public void FailedBeginNeverEndsOrRunsMutation()
    {
        var calls = new List<string>(); var cleanup = new CleanupRecorder();
        Assert.Throws<IOException>(() => Run(() => { calls.Add("begin"); throw new IOException("begin"); }, () => calls.Add("body"), () => calls.Add("end"), cleanup));
        Assert.Equal(new[] { "begin" }, calls); Assert.True(cleanup.Completed);
    }
    [Fact]
    public void MutationFailureRetainsPrimaryErrorAndClosesExactlyOnce()
    {
        var calls = new List<string>(); var cleanup = new CleanupRecorder();
        var error = Assert.Throws<IOException>(() => Run(() => calls.Add("begin"), () => { calls.Add("body"); throw new IOException("primary"); }, () => { calls.Add("end"); throw new InvalidOperationException("cleanup"); }, cleanup));
        Assert.Equal("primary", error.Message); Assert.Equal(new[] { "begin", "body", "end" }, calls); Assert.False(cleanup.Completed);
    }
    private static void Run(Action begin, Action body, Action end, CleanupRecorder cleanup)
        => CommandGroup.Run(begin, body, end, cleanup);
}
