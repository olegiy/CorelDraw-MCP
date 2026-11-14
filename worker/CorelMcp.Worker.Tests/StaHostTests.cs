using System.Text.Json;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class StaHostTests
{
    private static WorkerRequest Request() => new(Guid.NewGuid().ToString(), "corel_status", JsonSerializer.SerializeToElement(new { }), "C:\\Test");

    [Fact]
    public async Task OneStaOwnsFactoryExecutionProgressAndCleanupWithNoHiddenQueue()
    {
        List<int> threads = [];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var dispatcher = new DelegateDispatcher((_, progress) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            progress("cleanup");
            return new WorkerResult(true);
        }, () => threads.Add(Environment.CurrentManagedThreadId));
        var host = new StaHost(() => { threads.Add(Environment.CurrentManagedThreadId); return dispatcher; });
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var result = new TaskCompletionSource<WorkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TrySubmit(Request(), _ => threads.Add(Environment.CurrentManagedThreadId), value => result.SetResult(value)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(host.TrySubmit(Request(), _ => { }, _ => throw new Exception("Скрытая очередь запрещена.")));
        host.CompleteInput();
        Assert.False(host.Completion.IsCompleted);
        release.Set();
        Assert.True((await result.Task.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, dispatcher.Executions);
        Assert.Equal(4, threads.Count);
        Assert.Single(threads.Distinct());
    }

    [Fact]
    public async Task MessagePumpingAndResultCallbacksCannotReenterDispatch()
    {
        StaHost? host = null;
        bool duringOperation = true, duringProgress = true, duringResult = true;
        var dispatcher = new DelegateDispatcher((_, progress) =>
        {
            duringOperation = host!.TrySubmit(Request(), _ => { }, _ => { });
            System.Windows.Forms.Application.DoEvents();
            progress("cleanup");
            System.Windows.Forms.Application.DoEvents();
            return new WorkerResult(true);
        });
        host = new StaHost(() => dispatcher);
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.TrySubmit(Request(), _ => duringProgress = host.TrySubmit(Request(), _ => { }, _ => { }), _ =>
        {
            duringResult = host.TrySubmit(Request(), _ => { }, _ => { });
            host.CompleteInput();
        }));
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(duringOperation);
        Assert.False(duringProgress);
        Assert.False(duringResult);
        Assert.Equal(1, dispatcher.Executions);
    }

    [Fact]
    public async Task UnexpectedOperationFailureDoesNotClaimNonexecutionOrReplay()
    {
        var dispatcher = new DelegateDispatcher((_, _) => throw new InvalidOperationException("Ошибка после возможной мутации."));
        var host = new StaHost(() => dispatcher);
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        WorkerResult? result = null;
        Assert.True(host.TrySubmit(Request(), _ => { }, value => { result = value; host.CompleteInput(); }));
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result!.Ok);
        Assert.Null(result.Error!.Applied);
        Assert.Equal(1, dispatcher.Executions);
    }

    [Fact]
    public async Task BrokenResultTransportStillRunsStaCleanupAndEndsWithoutReplay()
    {
        int cleanup = 0;
        var dispatcher = new DelegateDispatcher((_, _) => new WorkerResult(true), () => cleanup++);
        var host = new StaHost(() => dispatcher);
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.TrySubmit(Request(), _ => { }, _ => throw new IOException("Потерян stdout.")));
        await Assert.ThrowsAsync<IOException>(() => host.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, dispatcher.Executions);
        Assert.Equal(1, cleanup);
    }

    private sealed class DelegateDispatcher(Func<WorkerRequest, Action<string>, WorkerResult> action, Action? cleanup = null) : IOperationDispatcher
    {
        public int Executions { get; private set; }
        public WorkerResult Execute(WorkerRequest request, Action<string> progress) { Executions++; return action(request, progress); }
        public void Dispose() => cleanup?.Invoke();
    }
}
