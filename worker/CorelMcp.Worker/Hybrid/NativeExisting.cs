using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

internal static class NativeExisting
{
    public static Action? TryPrepare(DrawingTarget target,IDrawingCommand command)
    {
        var c=target.Context;if(c.Session.ExecutionMode!=ExecutionMode.Hybrid)return null;
        if(command is CreateShapesCommand create)
        {
            var decision=HybridPolicy.Select(command,new(create.Items.Length,true,false),ExecutionMode.Hybrid);
            if(decision.Engine!=ExecutionEngine.Vba)return null;
            var batch=new CreateBatchCommand(create.LayerId,create.Items.Select((item,i)=>new BatchCreateItem("item-"+i,new CreateShapesCommand(create.LayerId,[item]),null,null,0)).ToArray());
            return NativeDrawing.Prepare(target,batch);
        }
        int[] ids=command switch {StyleCommand style=>style.StaticIds,MoveCommand move=>move.StaticIds,_=>[]};
        if(ids.Length<20)return null;
        var shapes=target.Resolve(ids,bitmap:command is MoveCommand);
        bool supported=shapes.All(s=>(int)s.Type is 1 or 2 or 3 or 4 or 6);
        long textUnits=0;
        foreach(var shape in shapes)if(shape.Type==VGCore.cdrShapeType.cdrTextShape)
        {
            var text=c.Track(c.Track(shape.Text).Story).WideText;textUnits+=text.Length;
            try{Validation.CorelText.Validate(text);}catch(Validation.TextValidationException){supported=false;}
        }
        supported&=textUnits<=100000;
        if(HybridPolicy.Select(command,new(ids.Length,supported,false),ExecutionMode.Hybrid).Engine!=ExecutionEngine.Vba)return null;
        var update=new UpdateBatchCommand(ids.Select(id=>command switch
        {
            StyleCommand style=>new BatchUpdateItem(id,style.Fill,style.Outline,null),
            MoveCommand move=>new BatchUpdateItem(id,null,null,new MoveCommand([id],move.DxMm,move.DyMm)),
            _=>throw new InvalidOperationException()
        }).ToArray());
        return NativeDrawing.Prepare(target,update,shapes);
    }
}
