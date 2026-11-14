using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker;

internal interface IOperationDispatcher : IDisposable
{
    WorkerResult Execute(WorkerRequest request, Action<string> progress);
}

internal sealed class OperationDispatcher : IOperationDispatcher
{
    private readonly WorkerSession session;
    private readonly CorelMcp.ApiMetadataSession metadata = new();
    public OperationDispatcher(WorkerSession? session = null) { this.session = session ?? new WorkerSession(); }
    public WorkerResult Execute(WorkerRequest request, Action<string> progress)
    {
        object arguments;
        try { arguments = OperationContracts.Parse(request.Tool, request.Args); WorkerArtifacts.EnsureDirectory(request.RunDirectory); }
        catch (OperationFailure error) { return new(false, Error: new WorkerError(error.Code, error.Message, "preflight", false), Cleanup: Dto.Of(("completed", true), ("errors", Array.Empty<object>()))); }
        var cleanup = new CleanupRecorder();
        ComOperationMutex? lease = null;
        OperationContext? context = null;
        StateScope? state = null;
        Exception? failure = null;
        object? result = null;
        Dictionary<string, string> versions = new() { ["worker"] = "0.4.0" };
        try
        {
            progress("waiting_for_mutex");
            lease = ComOperationMutex.Acquire();
            context = new OperationContext(session, request, progress, cleanup);
            context.Stage("connecting");
            _ = context.App;
            if(session.NativeOutcomeUnknown&&arguments is not (StatusArguments or InspectArguments or PreviewArguments or ExportArguments or MeasureArguments or IReadOnlyDocumentArguments or DescribeApiArguments))
                throw new OperationFailure("NATIVE_SESSION_BLOCKED","Предыдущий исход native batch неизвестен; новые мутации запрещены до сверки состояния.");
            if (session.CorelVersion is not null) versions["coreldraw"] = session.CorelVersion;
            if (arguments is IDocumentArguments doc)
            {
                context.Target = context.Registry.Get(context.App, context.Scope, doc.DocumentId);
                if (arguments is not CloseDocumentArguments) state = new StateScope(context, arguments is InspectArguments or MeasureArguments or PreviewArguments or ExportArguments or IReadOnlyDocumentArguments
                    || arguments is DrawingArguments { Command: LayoutCommand { DryRun: true } });
            }
            result = arguments switch
            {
                StatusArguments => Documents.Status(context),
                CreateDocumentArguments create => Documents.Create(context, create),
                OpenDocumentArguments open => Documents.Open(context, open),
                SaveDocumentArguments save => Documents.Save(context, save),
                CloseDocumentArguments close => Documents.Close(context, close),
                InspectArguments inspect => Inspection.Execute(context, inspect),
                PreviewArguments preview => Export.Preview(context, preview),
                ExportArguments export => Export.Execute(context, export),
                MeasureArguments measure => GeometrySnapshot.Execute(context, measure),
                DrawingArguments drawing => DrawingOperation.Execute(context, drawing),
                InspectCurveArguments curve => Curves.Inspect(context, curve),
                FindTextArguments find => Text.Find(context, find),
                PreflightArguments preflight => Preflight.Execute(context, preflight),
                DescribeApiArguments api => Describe(context, api),
                _ => throw new OperationFailure("UNKNOWN_TOOL", "Неподдерживаемый инструмент.")
            };
            result = WorkerArtifacts.PrepareResult(request.RunDirectory, result, context.Artifacts);
        }
        catch (Exception error)
        {
            failure = error;
            if(context is not null&&error.Data["artifact_paths"] is string[] paths)
                foreach(var path in paths)
                    if(Path.GetFullPath(path).StartsWith(Path.GetFullPath(request.RunDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(path))context.Artifacts.Add(path);
        }
        finally
        {
            context?.ReleaseResources();
            if (state is not null) cleanup.Try("state_scope", state.Dispose);
            if (context is not null) cleanup.Try("com_scope", context.Dispose);
            if (lease is not null) cleanup.Try("mutex", lease.Dispose);
        }
        WorkerError? errorDto = null;
        if (!cleanup.Completed && arguments is MeasureArguments)
        {
            if (result is Dictionary<string, object?> details) details["original_state_preserved"] = false;
            failure ??= new OperationFailure("GEOMETRY_CLEANUP_FAILED", "Сохранность исходного состояния после измерения не подтверждена.", null);
        }
        if(!cleanup.Completed)failure ??= new OperationFailure("CLEANUP_FAILED","Восстановление состояния CorelDRAW не подтверждено; известные изменения сохранены, повтор запрещён.",null);
        if (failure is not null)
        {
            string code = failure switch { OperationFailure operation => operation.Code, CorelConnectionException connection => connection.Code, ComMutexException mutex => mutex.Code, _ => failure.Data["code"] as string ?? "COM_ERROR" };
            bool? applied = context?.MutationConfirmed == true ? true : context?.MutationStarted == true || !cleanup.Completed ? null : false;
            if(code=="NATIVE_OUTCOME_UNKNOWN")applied=null;
            if (arguments is DrawingArguments && context?.MutationStarted == true)
            {
                result = context.DrawingResult(true);
                try { result = WorkerArtifacts.PrepareResult(request.RunDirectory, result, context.Artifacts); }
                catch (Exception artifactError)
                {
                    cleanup.Add("partial_result_artifact", artifactError);
                    context.DrawingItems.Clear();
                    var bounded = (Dictionary<string, object?>)context.DrawingResult(true);
                    bounded["result_details_unavailable"] = true; result = bounded;
                }
            }
            if (failure is CorelConnectionException { CleanupError: not null } connectionError) cleanup.Add("connection", connectionError.CleanupError);
            errorDto = new WorkerError(code, failure.Message, context?.CurrentStage ?? "waiting_for_mutex", applied, failure.HResult, failure.Source);
        }
        versions["execution_engine"]=context?.ExecutionEngine??"csharp";
        versions["route_policy"]=Hybrid.HybridPolicy.Version;
        if(session.NativeIdentity is not null)versions["native_module"]=session.NativeIdentity.Version;
        return new WorkerResult(failure is null, Result: result, Error: errorDto, Cleanup: cleanup.Snapshot(),
            Artifacts: context?.Artifacts.ToArray() ?? [], Warnings:context?.Warnings.ToArray()??[], Versions: versions);
    }
    private object Describe(OperationContext context, DescribeApiArguments args)
    {
        context.Stage("describing_api");
        return metadata.Describe(context.App, args.Kind, args.Name ?? "", args.Filter, args.Offset, args.Limit);
    }
    public void Dispose()
    {
        metadata.Clear();
        if (session.Connected) { using var lease = ComOperationMutex.Acquire(); session.Dispose(); }
        else session.Dispose();
    }
}
