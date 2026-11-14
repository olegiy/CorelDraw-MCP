using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class OperationCleanupTests
{
    [Fact]
    public void DeferredCleanupRunsOnceEvenWhenPreparedMutationWasNeverEntered()
    {
        using var session=new WorkerSession();var cleanup=new CleanupRecorder();var calls=new List<string>();
        var context=new OperationContext(session,new(Guid.NewGuid().ToString(),"test",System.Text.Json.JsonSerializer.SerializeToElement(new{}),"C:\\unused"),_=>{},cleanup);
        var method=typeof(OperationContext).GetMethod("DeferCleanup");Assert.NotNull(method);
        method.Invoke(context,["first",(Action)(()=>calls.Add("first"))]);
        method.Invoke(context,["second",(Action)(()=>{calls.Add("second");throw new IOException("cleanup failure");})]);
        context.Dispose();context.Dispose();
        Assert.Equal(new[]{"second","first"},calls);Assert.False(cleanup.Completed);
    }
}
