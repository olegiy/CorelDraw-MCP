namespace CorelMcp.Worker;

internal sealed class StaHost
{
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Control? control;
    private ApplicationContext? context;
    private IOperationDispatcher? dispatcher;
    private Exception? failure;
    private bool busy;
    private bool closing;
    private string? activeJobId;
    public string? ActiveJobId { get { lock (gate) return activeJobId; } }
    public Task Ready => ready.Task;
    public Task Completion => completion.Task;

    public StaHost(Func<IOperationDispatcher> factory)
    {
        var thread = new Thread(() => Run(factory)) { Name = "CorelMcp STA", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run(Func<IOperationDispatcher> factory)
    {
        try
        {
            using var ownedContext = new ApplicationContext();
            using var ownedControl = new Control();
            _ = ownedControl.Handle;
            dispatcher = factory();
            lock (gate) { context = ownedContext; control = ownedControl; }
            ownedControl.BeginInvoke((Action)(() => { ready.TrySetResult(); ExitWhenIdle(); }));
            System.Windows.Forms.Application.Run(ownedContext);
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            try { dispatcher?.Dispose(); } catch (Exception error) { failure ??= error; }
            lock (gate) { control = null; context = null; closing = true; }
            if (failure is not null) { ready.TrySetException(failure); completion.TrySetException(failure); }
            else { ready.TrySetResult(); completion.TrySetResult(); }
        }
    }

    public bool TrySubmit(WorkerRequest request, Action<string> progress, Action<WorkerResult> result)
    {
        lock (gate)
        {
            if (closing || busy || !ready.Task.IsCompletedSuccessfully || control is null) return false;
            busy = true;
            activeJobId = request.JobId;
            try { control.BeginInvoke((Action)(() => Execute(request, progress, result))); }
            catch { busy = false; activeJobId = null; return false; }
            return true;
        }
    }

    private void Execute(WorkerRequest request, Action<string> progress, Action<WorkerResult> result)
    {
        try
        {
            WorkerResult outcome;
            try { outcome = dispatcher!.Execute(request, progress); }
            catch (Exception error)
            {
                outcome = new WorkerResult(false, Error: new WorkerError("UNEXPECTED_OPERATION_ERROR",
                    "Ошибка операции; отсутствие изменений не подтверждено. Автоматический повтор запрещён.", "executing", Number: error.HResult));
            }
            // Keep the guard through serialization and result transport, including pumped callbacks.
            result(outcome);
        }
        catch (Exception error) { lock (gate) { failure ??= error; closing = true; } }
        finally { lock (gate) { busy = false; activeJobId = null; } ExitWhenIdle(); }
    }

    public void CompleteInput()
    {
        lock (gate)
        {
            if (closing) return;
            closing = true;
            if (control is not null) control.BeginInvoke((Action)ExitWhenIdle);
        }
    }

    private void ExitWhenIdle()
    {
        lock (gate) { if (!closing || busy) return; }
        // Dispose COM state on the same STA while its message loop still exists.
        var previous = dispatcher;
        dispatcher = null;
        try { previous?.Dispose(); } catch (Exception error) { failure ??= error; }
        context?.ExitThread();
    }
}
