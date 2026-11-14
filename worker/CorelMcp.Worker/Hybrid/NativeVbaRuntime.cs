using CorelMcp.Worker.Interop;

namespace CorelMcp.Worker.Hybrid;

internal sealed record NativeModuleIdentity(string ProjectName, string Version, string SourceSha256);
internal interface INativeVbaRuntime:IDisposable
{
    NativeModuleIdentity? LastIdentity { get; }
    NativeModuleIdentity EnsureReady(OperationContext context);
    void RequireOperation(string operation);
    void ValidateFonts(OperationContext context,string[] names);
    NativeBatchReply Apply(PreparedNativeBatch batch);
    string ExtractGeometry(OperationContext c,VGCore.Document doc,VGCore.ShapeRange targets,int[] ids,double mm,string nonce);
}

internal sealed class NativeVbaRuntime(string directory) : INativeVbaRuntime
{
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private VerifiedNativeModule? verified;
    private bool disposed;
    public NativeModuleIdentity? LastIdentity { get; private set; }
    private void Owner()
    {
        if(Environment.CurrentManagedThreadId!=ownerThread) throw new InvalidOperationException("VBA runtime принадлежит другому STA-потоку.");
    }
    public NativeModuleIdentity EnsureReady(OperationContext context)=>EnsureReady(context.App,context.Scope);
    public void RequireOperation(string operation)
    {
        Owner();ObjectDisposedException.ThrowIf(disposed,this);
        if(verified is null||!verified.Description.Operations.Contains(operation,StringComparer.Ordinal))
            throw new CorelMcp.Worker.Operations.OperationFailure("NATIVE_CAPABILITY_UNAVAILABLE","Готовый VBA модуль не поддерживает эту операцию.");
    }
    public void ValidateFonts(OperationContext c,string[] names)
    {
        if(names.Length==0)return;
        RequireOperation("validate_fonts");string nonce=Guid.NewGuid().ToString("N");
        object[] parameters=[names,nonce];var manager=c.Track(c.App.GMSManager);
        var raw=manager.RunMacro(verified!.ProjectName,"M_Batch.ValidateFonts",ref parameters) as string;
        if(raw is null||raw.Length>65536)throw new InvalidDataException("Шрифты native batch не подтверждены.");
        using var reply=System.Text.Json.JsonDocument.Parse(raw);
        var properties=reply.RootElement.EnumerateObject().ToArray();
        if(properties.Length!=3||properties.Select(p=>p.Name).Distinct().Count()!=3
            ||reply.RootElement.GetProperty("nonce").GetString()!=nonce||reply.RootElement.GetProperty("valid").ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new InvalidDataException("Шрифты native batch не подтверждены.");
        string code=reply.RootElement.GetProperty("error_code").GetString()??throw new InvalidDataException("Нет статуса проверки шрифтов.");
        if(!reply.RootElement.GetProperty("valid").GetBoolean())
            throw new CorelMcp.Worker.Operations.OperationFailure(code=="FONT_NOT_INSTALLED"?code:"FONT_VALIDATION_FAILED","Шрифт native batch не подтверждён; пакет не применён.");
        if(code!="")throw new InvalidDataException("Противоречивый статус проверки шрифтов.");
    }
    public NativeBatchReply Apply(PreparedNativeBatch batch)
    {
        RequireOperation(batch.Operation);
        string nonce=Guid.NewGuid().ToString("N");
        object[] parameters=[batch.Document,batch.Page,batch.Layer,batch.Targets,batch.Operation=="create_batch"?1:batch.Operation=="convert_text"?3:2,batch.Rows,nonce,batch.Left,batch.Bottom,batch.Factor];
        try
        {
            var manager=batch.Context.Track(batch.Context.App.GMSManager);
            var raw=manager.RunMacro(verified!.ProjectName,"M_Batch.Apply",ref parameters) as string;
            if(raw is null)throw new InvalidDataException("Нет результата native batch.");
            return NativeBatchResults.Read(raw,nonce,batch.Operation,batch.Keys,batch.OriginalIds);
        }
        catch(Exception error)
        {
            batch.Context.Session.MarkNativeOutcomeUnknown();
            throw new CorelMcp.Worker.Operations.OperationFailure("NATIVE_OUTCOME_UNKNOWN","Результат VBA-вызова не подтверждён; повтор запрещён: "+error.Message,null);
        }
    }
    public string ExtractGeometry(OperationContext c,VGCore.Document doc,VGCore.ShapeRange targets,int[] ids,double mm,string nonce)
    {
        RequireOperation("extract_geometry");object[] parameters=[doc,targets,ids,mm,nonce];
        var manager=c.Track(c.App.GMSManager);
        var raw=manager.RunMacro(verified!.ProjectName,"M_Geometry.Extract",ref parameters) as string
            ??throw new InvalidDataException("Нативный снимок геометрии не получен.");
        if(raw.Length>NativeGeometry.MaxBytes)throw new InvalidDataException("Снимок превышает лимит.");
        using var reply=System.Text.Json.JsonDocument.Parse(raw);
        if(reply.RootElement.TryGetProperty("error_code",out var error))
        {
            var fields=reply.RootElement.EnumerateObject().Select(p=>p.Name).ToArray();
            if(fields.Length!=3||fields.Distinct().Count()!=3||reply.RootElement.GetProperty("protocol").GetInt32()!=1||reply.RootElement.GetProperty("nonce").GetString()!=nonce)
                throw new InvalidDataException("Идентичность отказа geometry не подтверждена.");
            string? code=error.GetString();throw new CorelMcp.Worker.Operations.OperationFailure(code is "GEOMETRY_LIMIT_EXCEEDED" or "UNSUPPORTED_GEOMETRY"?code:"NATIVE_GEOMETRY_FAILED","Нативное извлечение в private document не завершено; исходник не изменялся.");
        }
        return raw;
    }
    internal NativeModuleIdentity EnsureReady(VGCore.Application app, ComScope scope)
    {
        Owner(); ObjectDisposedException.ThrowIf(disposed,this); verified??=NativeModuleManifest.LoadAndVerify(directory);
        var manager=scope.Track(app.GMSManager);var projects=scope.Track(manager.Projects);
        VGCore.GMSProject? selected=null;
        for(int i=1;i<=projects.Count;i++)
        {
            var p=scope.Track(projects[i]);
            if(p.Name!=verified.ProjectName) continue;
            if(selected is not null || !string.Equals(Path.GetFullPath(p.FullFileName),verified.FullPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Обнаружен другой VBA-проект с именем runtime; вызов запрещён.");
            selected=p;
        }
        selected??=scope.Track(projects.Load(verified.FullPath,false,false));
        if(selected.Name!=verified.ProjectName || !string.Equals(Path.GetFullPath(selected.FullFileName),verified.FullPath,StringComparison.OrdinalIgnoreCase)
            || selected.Dirty) throw new InvalidDataException("Идентичность загруженного VBA runtime не подтверждена.");
        string nonce=Guid.NewGuid().ToString("N");object[] parameters=[nonce];
        object reply=manager.RunMacro(verified.ProjectName,"M_Bridge.Handshake",ref parameters);
        if(reply is not string raw) throw new InvalidDataException("VBA runtime не вернул handshake.");
        var valid=NativeCallCodec.ReadHandshake(raw,nonce,verified.Description);
        return LastIdentity=new(verified.ProjectName,valid.ModuleVersion,valid.SourceSha256);
    }
    public void Dispose() { Owner(); verified?.Dispose(); verified=null; disposed=true; }
}
