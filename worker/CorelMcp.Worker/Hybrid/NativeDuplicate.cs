using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

// Internal prepared data only; never accepted by the public batch parser.
internal sealed record NativeCloneCommand(int SourceIndex,int SourceStaticId,double DxMm,double DyMm):IDrawingCommand;
internal static class NativeDuplicate
{
    public static Action? TryPrepare(DrawingTarget target,DuplicateCommand command)
    {
        var c=target.Context;
        if(c.Session.ExecutionMode!=ExecutionMode.Hybrid||(long)command.StaticIds.Length*command.Copies<20)return null;
        var sources=target.Resolve(command.StaticIds,bitmap:true);
        bool supported=sources.All(s=>(int)s.Type is 1 or 2 or 3 or 4 or 6);
        long textUnits=0;
        foreach(var s in sources)if(s.Type==VGCore.cdrShapeType.cdrTextShape)
        {
            string text=c.Track(c.Track(s.Text).Story).WideText;textUnits+=text.Length;
            try{Validation.CorelText.Validate(text);}catch(Validation.TextValidationException){supported=false;}
        }
        if(!supported||textUnits*command.Copies>100000)return null;
        int count=checked(sources.Length*command.Copies);
        if(count>1000)throw new OperationFailure("RESULT_LIMIT_EXCEEDED","Дублирование создаст более 1000 фигур.");
        if(HybridPolicy.Select(command,new(sources.Length,true,false,count),ExecutionMode.Hybrid).Engine!=ExecutionEngine.Vba)return null;
        var runtime=c.Session.Native;runtime.EnsureReady(c);runtime.RequireOperation("duplicate");
        var layerId=c.Registry.LayerId(c.Target!,target.Page,c.Track(sources[0].Layer));
        var items=Enumerable.Range(0,count).Select(i=>new BatchCreateItem("copy-"+i,
            new NativeCloneCommand(i%sources.Length,sources[i%sources.Length].StaticID,command.StepXMm*(i/sources.Length+1),command.StepYMm*(i/sources.Length+1)),null,null,0)).ToArray();
        return NativeDrawing.Prepare(target,new CreateBatchCommand(layerId,items),sources);
    }
}
