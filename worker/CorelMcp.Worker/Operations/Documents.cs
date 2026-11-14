using CorelMcp.Worker.Interop;

namespace CorelMcp.Worker.Operations;

internal static class Documents
{
    public static object Status(OperationContext context)
    {
        if(context.Session.ExecutionMode==Hybrid.ExecutionMode.Hybrid)
        {
            context.Stage("native_readiness");context.Session.Native.EnsureReady(context);
        }
        var app = context.App;
        var entries = context.Registry.Sync(app, context.Scope);
        string? active = null;
        if (entries.Count > 0)
        {
            var current = ComScope.Identity(context.Track(app.ActiveDocument));
            active = entries.Single(entry => ComScope.Identity(entry.Document) == current).Id;
        }
        return Dto.Of(("version", app.Version), ("session_id", context.Session.SessionId), ("active_document_id", active),
            ("documents", entries.Select(entry => context.Registry.Info(entry, context.Scope)).ToArray()),
            ("application_state", Dto.Of(("optimization", app.Optimization), ("events_enabled", app.EventsEnabled))),
            ("executor", Dto.Of(("state", context.Session.NativeOutcomeUnknown?"blocked_unknown":"ready"), ("backend", "csharp"),
                ("execution_mode",context.Session.ExecutionMode.ToString().ToLowerInvariant()),("native_module",context.Session.NativeIdentity?.Version), ("pid", Environment.ProcessId),
                ("apartment", Thread.CurrentThread.GetApartmentState().ToString()), ("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription), ("protocol_version", 1))),
            ("capabilities", Dto.Of(("typed_operations", true), ("arbitrary_code", false))));
    }
    public static object Create(OperationContext context, CreateDocumentArguments args)
    {
        context.Stage("creating_document"); context.Mutating();
        var doc = context.Track(context.App.CreateDocument());
        var entry = context.Registry.Register(context.App, context.Scope, doc);
        doc.Unit = VGCore.cdrUnit.cdrMillimeter;
        context.Track(doc.ActivePage).SetSize(args.WidthMm, args.HeightMm);
        return context.Registry.Info(entry, context.Scope);
    }
    public static object Open(OperationContext context, OpenDocumentArguments args)
    {
        context.Stage("opening_document");
        var path = Path.GetFullPath(args.Path);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".cdr", StringComparison.OrdinalIgnoreCase))
            throw new OperationFailure("CDR_NOT_FOUND", "Нужен существующий файл .cdr.");
        var entry = context.Registry.Sync(context.App, context.Scope).FirstOrDefault(item => string.Equals(item.Document.FullFileName, path, StringComparison.OrdinalIgnoreCase));
        context.Mutating();
        if (entry is not null) entry.Document.Activate();
        else entry = context.Registry.Register(context.App, context.Scope, context.Track(context.App.OpenDocument(path)));
        return context.Registry.Info(entry, context.Scope);
    }
    public static object Save(OperationContext context, SaveDocumentArguments args)
    {
        var doc = context.Target!.Document;
        var current = doc.FullFileName ?? "";
        var requested = args.Path ?? current;
        if (string.IsNullOrEmpty(requested) || !Path.IsPathFullyQualified(requested)) throw new OperationFailure("SAVE_PATH_REQUIRED", "Для несохранённого документа нужен абсолютный путь к .cdr.");
        var path = OutputPath(requested, "cdr");
        foreach (var entry in context.Registry.Sync(context.App, context.Scope))
            if (entry.Id != args.DocumentId && string.Equals(entry.Document.FullFileName, path, StringComparison.OrdinalIgnoreCase))
                throw new OperationFailure("DOCUMENT_ALREADY_OPEN", "Файл назначения открыт другим документом.");
        context.Stage("saving"); context.Mutating();
        if (string.Equals(current, path, StringComparison.OrdinalIgnoreCase)) doc.Save();
        else
        {
            var options = context.Track(context.App.CreateStructSaveAsOptions());
            options.Overwrite = true; options.Filter = VGCore.cdrFilter.cdrCDR;
            doc.SaveAs(path, options);
        }
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new OperationFailure("SAVE_NOT_CREATED", "CorelDRAW не создал непустой CDR.", null);
        context.Artifacts.Add(path);
        return Dto.Of(("document", context.Registry.Info(context.Target, context.Scope)), ("path", path), ("bytes", new FileInfo(path).Length));
    }
    public static object Close(OperationContext context, CloseDocumentArguments args)
    {
        var doc = context.Target!.Document;
        if (doc.Dirty && !args.DiscardChanges) throw new OperationFailure("UNSAVED_CHANGES", "Сохраните документ или явно передайте discard_changes=true.");
        context.Stage("closing_document"); context.Mutating();
        if (args.DiscardChanges) doc.Dirty = false;
        doc.Close(); context.Registry.Sync(context.App, context.Scope);
        return Dto.Of(("closed", true), ("document_id", args.DocumentId));
    }
    public static string OutputPath(string requested, string format)
    {
        var path = Path.GetFullPath(requested);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != "." + format && !(format == "jpg" && extension == ".jpeg") && !(format == "tiff" && extension == ".tif"))
            throw new OperationFailure("INVALID_EXTENSION", "Расширение назначения не соответствует формату.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new OperationFailure("DIRECTORY_NOT_FOUND", "Родительская папка назначения должна существовать.");
        if (Directory.Exists(path)) throw new OperationFailure("PATH_IS_DIRECTORY", "Назначение является папкой.");
        return path;
    }
}
