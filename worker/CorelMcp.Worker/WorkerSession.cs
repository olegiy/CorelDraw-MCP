using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;
using CorelMcp.Worker.Hybrid;

namespace CorelMcp.Worker;

internal sealed class WorkerSession(ExecutionMode? mode=null,Func<INativeVbaRuntime>? nativeFactory=null) : IDisposable
{
    private readonly CorelConnection connection = new();
    private readonly ComOwnership ownership = new();
    private DocumentRegistry? registry;
    private INativeVbaRuntime? native;
    public ExecutionMode ExecutionMode { get; } = mode??ParseExecutionMode(Environment.GetEnvironmentVariable("CORELDRAW_MCP_EXECUTION_MODE"));
    public bool NativeOutcomeUnknown { get; private set; }
    public void MarkNativeOutcomeUnknown()=>NativeOutcomeUnknown=true;
    public INativeVbaRuntime Native=>native??=nativeFactory?.Invoke()??new NativeVbaRuntime(Path.Combine(AppContext.BaseDirectory,"native"));
    public NativeModuleIdentity? NativeIdentity=>native?.LastIdentity;
    private static ExecutionMode ParseExecutionMode(string? value)=>value switch
    {
        null or "csharp"=>ExecutionMode.CSharp,"hybrid"=>ExecutionMode.Hybrid,
        _=>throw new ArgumentException("Допустим execution_mode csharp или hybrid.")
    };
    public bool Connected => connection.SessionId is not null;
    public string? CorelVersion => connection.Version;
    public ComScope CreateScope() => ownership.CreateScope();
    public VGCore.Application Application
    {
        get
        {
            if (connection.SessionId is null) connection.Connect("CorelDRAW.Application.27");
            return connection.GetApplication();
        }
    }
    public DocumentRegistry Registry => registry ??= new DocumentRegistry(connection.SessionId
        ?? throw new OperationFailure("NOT_CONNECTED", "Сначала требуется сеанс CorelDRAW."), ownership);
    public string SessionId => connection.SessionId ?? throw new OperationFailure("NOT_CONNECTED", "Сеанс не подключён.");
    public void Dispose()
    {
        try { native?.Dispose(); registry?.Dispose(); } finally { connection.Dispose(); }
    }
}

internal sealed class OperationContext(WorkerSession session, WorkerRequest request, Action<string> progress, CleanupRecorder cleanup) : IDisposable
{
    public WorkerSession Session { get; } = session;
    public WorkerRequest Request { get; } = request;
    public ComScope Scope { get; } = session.CreateScope();
    public VGCore.Application App => Session.Application;
    public DocumentRegistry Registry => Session.Registry;
    public DocumentRegistry.Entry? Target { get; set; }
    public CleanupRecorder Cleanup { get; } = cleanup;
    public List<string> Artifacts { get; } = [];
    public List<string> Warnings { get; } = [];
    private readonly List<(string step, Action action)> deferredCleanup = [];
    public void DeferCleanup(string step, Action action) => deferredCleanup.Add((step, action));
    public void ReleaseResources()
    {
        var pending = deferredCleanup.ToArray(); deferredCleanup.Clear();
        for (int index = pending.Length - 1; index >= 0; index--) Cleanup.Try(pending[index].step, pending[index].action);
    }
    public string CurrentStage { get; private set; } = "starting";
    public string ExecutionEngine { get; set; } = "csharp";
    public bool MutationStarted { get; private set; }
    public bool MutationConfirmed { get; private set; }
    private readonly HashSet<int> affected = [], created = [], deleted = [];
    public List<object> DrawingItems { get; } = [];
    public Dictionary<string, object?> DrawingDetails { get; } = [];
    public void RecordMutation(IEnumerable<int> before, IEnumerable<int> after)
    {
        var oldIds = before.ToHashSet(); var newIds = after.ToHashSet();
        affected.UnionWith(oldIds); affected.UnionWith(newIds);
        created.UnionWith(newIds.Except(oldIds)); deleted.UnionWith(oldIds.Except(newIds));
        MutationConfirmed = true;
    }
    public void RecordKnownChanges(IEnumerable<int> knownAffected,IEnumerable<int> knownCreated,IEnumerable<int> knownDeleted)
    {
        var a=knownAffected.ToArray();var c=knownCreated.ToArray();var d=knownDeleted.ToArray();
        affected.UnionWith(a);affected.UnionWith(c);affected.UnionWith(d);created.UnionWith(c);deleted.UnionWith(d);
        if(a.Length+c.Length+d.Length>0)MutationConfirmed=true;
    }
    public object DrawingResult(bool partial)
    {
        var result = Dto.Of(("document_id", Target!.Id), ("partial", partial), ("affected_static_ids", affected.Order().ToArray()),
            ("created_static_ids", created.Order().ToArray()), ("deleted_static_ids", deleted.Order().ToArray()), ("items", DrawingItems.ToArray()));
        foreach (var item in DrawingDetails) result[item.Key] = item.Value;
        return result;
    }
    public void Mutating() => MutationStarted = true;
    public void Stage(string stage) { CurrentStage = stage; progress(stage); }
    public T Track<T>(T value) where T : class => Scope.Track(value);
    public void Dispose() { ReleaseResources(); Scope.Dispose(); }
}

internal sealed class CleanupRecorder
{
    private readonly List<object> errors = [];
    public List<int> MissingSelectionIds { get; } = [];
    public bool Completed => errors.Count == 0;
    public void Try(string step, Action action)
    {
        try { action(); } catch (Exception error) { errors.Add(Dto.Of(("step", step), ("code", "CLEANUP_ERROR"), ("message", error.Message))); }
    }
    public void Add(string step, Exception error) => errors.Add(Dto.Of(("step", step), ("code", "CLEANUP_ERROR"), ("message", error.Message)));
    public Dictionary<string, object?> Snapshot() => Dto.Of(("completed", Completed), ("errors", errors.ToArray()),
        ("selection_missing_static_ids", MissingSelectionIds.ToArray()), ("selection_restored_completely", MissingSelectionIds.Count == 0));
}
