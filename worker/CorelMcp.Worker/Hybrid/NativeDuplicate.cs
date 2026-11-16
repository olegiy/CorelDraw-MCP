using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

// Internal prepared data only; never accepted by the public batch parser.
internal sealed record NativeCloneCommand(int SourceIndex,int SourceStaticId,double DxMm,double DyMm):IDrawingCommand;
internal static class NativeDuplicate
{
    public static Action? TryPrepare(DrawingTarget target,DuplicateCommand command)
    {
        if(target.Context.Session.ExecutionMode!=ExecutionMode.Hybrid)return null;
        var sources=target.Resolve(command.StaticIds,bitmap:true);
        var offsets=Enumerable.Range(1,command.Copies).Select(i=>new PointMm(command.StepXMm*i,command.StepYMm*i)).ToArray();
        return PrepareCopies(target,command,sources,offsets,new(0,0),null);
    }

    public static Action? TryPrepare(DrawingTarget target,GridCommand command,VGCore.Shape[] sources,PointMm[] offsets,Action<VGCore.Shape[]> observed)
    {
        if(target.Context.Session.ExecutionMode!=ExecutionMode.Hybrid||offsets.Length==1)return null;
        var copies=offsets.Skip(1).Select(p=>new PointMm(p.XMm-offsets[0].XMm,p.YMm-offsets[0].YMm)).ToArray();
        return PrepareCopies(target,command,sources,copies,offsets[0],observed);
    }

    private sealed record Node(int Type,int ParentIndex,long LayerIdentity,BoundsMm Bounds,double Rotation,string Content);

    private static Action? PrepareCopies(DrawingTarget target,IDrawingCommand command,VGCore.Shape[] sources,PointMm[] offsets,PointMm sourceMove,Action<VGCore.Shape[]>? observed)
    {
        var c=target.Context;bool supported=true;long textUnits=0;
        var snapshots=sources.Select(root=>
        {
            if(root.ParentGroup is { } parent){c.Track(parent);supported=false;}
            var tree=new[]{new ShapeTraversal.Item(root,null,0)}.Concat(root.Type==VGCore.cdrShapeType.cdrGroupShape
                ?ShapeTraversal.Walk(c,c.Track(root.Shapes)).Select(item=>new ShapeTraversal.Item(item.Shape,item.ParentId??root.StaticID,item.Depth+1)):[]).ToArray();
            var indices=new Dictionary<int,int>();var nodes=new Node[tree.Length];
            for(int i=0;i<tree.Length;i++)
            {
                var shape=tree[i].Shape;
                if(!indices.TryAdd(shape.StaticID,i))throw new OperationFailure("AMBIGUOUS_STATIC_ID","StaticID не уникален в дереве источника.");
                var b=Geometry(target,shape);
                var bounds=b with {LeftMm=b.LeftMm+sourceMove.XMm,BottomMm=b.BottomMm+sourceMove.YMm};
                if(!double.IsFinite(bounds.LeftMm)||!double.IsFinite(bounds.BottomMm)||!double.IsFinite(bounds.WidthMm)||!double.IsFinite(bounds.HeightMm))
                    throw new OperationFailure("INVALID_GEOMETRY","Не подтверждена конечная геометрия источника копии.");
                int type=(int)shape.Type;
                nodes[i]=new(type,tree[i].ParentId is { } id?indices[id]:-1,ComScope.Identity(c.Track(shape.Layer)),bounds,shape.RotationAngle,Content(target,shape,type));
                supported&=nodes[i].Type is 1 or 2 or 3 or 4 or 6 or 7;
                if(nodes[i].Type!=7)supported&=(int)c.Track(shape.Fill).Type is 0 or 1 && (int)c.Track(shape.Outline).Type is 0 or 1;
                if(shape.Type==VGCore.cdrShapeType.cdrTextShape)
                {
                    var text=c.Track(c.Track(shape.Text).Story).WideText;textUnits+=text.Length;
                    try{Validation.CorelText.Validate(text);}catch(Validation.TextValidationException){supported=false;}
                }
            }
            return nodes;
        }).ToArray();
        long work=snapshots.Sum(s=>(long)s.Length)*offsets.Length;
        if(work>1000)throw new OperationFailure("RESULT_LIMIT_EXCEEDED","Превышен лимит новых объектов с учётом потомков групп.");
        supported&=textUnits*offsets.Length<=100000;
        if(HybridPolicy.Select(command,new(sources.Length,supported,false,checked((int)work)),ExecutionMode.Hybrid).Engine!=ExecutionEngine.Vba)return null;
        foreach(var p in offsets)
            if(!double.IsFinite(p.XMm)||!double.IsFinite(p.YMm)||!double.IsFinite(target.U(p.XMm))||!double.IsFinite(target.U(p.YMm)))
                throw new OperationFailure("INVALID_GEOMETRY","Нечисловое смещение копии.");
        var baseline=new HashSet<int>();
        foreach(var item in ShapeTraversal.Walk(c,c.Track(target.Page.Shapes)))
            if(item.Shape.StaticID<=0||!baseline.Add(item.Shape.StaticID))throw new OperationFailure("AMBIGUOUS_STATIC_ID","StaticID не уникален на странице.");
        var items=Enumerable.Range(0,sources.Length*offsets.Length).Select(i=>new BatchCreateItem("copy-"+i,
            new NativeCloneCommand(i%sources.Length,sources[i%sources.Length].StaticID,offsets[i/sources.Length].XMm,offsets[i/sources.Length].YMm),null,null,0)).ToArray();
        c.Session.Native.EnsureReady(c);c.Session.Native.RequireOperation("duplicate");
        return NativeDrawing.Prepare(target,new CreateBatchCommand(c.Registry.LayerId(c.Target!,target.Page,c.Track(sources[0].Layer)),items),sources,reply=>
        {
            var copies=VerifyCopies(target,reply,snapshots,offsets,baseline);
            observed?.Invoke(copies);
        });
    }

    private static VGCore.Shape[] VerifyCopies(DrawingTarget target,NativeBatchReply reply,Node[][] sources,PointMm[] offsets,HashSet<int> baseline)
    {
        var ids=reply.CreatedStaticIds;
        if(ids.Length>sources.Length*offsets.Length||ids.Distinct().Count()!=ids.Length||ids.Any(baseline.Contains)
            ||!reply.Items.Select(item=>item.GetProperty("static_id").GetInt32()).SequenceEqual(ids.Take(reply.Items.Length)))throw new InvalidDataException("Не подтверждены новые root ID и порядок копий.");
        var roots=ids.Select((id,index)=>(id,index)).ToDictionary(p=>p.id,p=>p.index);
        var result=new VGCore.Shape[ids.Length];var counts=new int[ids.Length];
        var seen=new HashSet<int>();var owners=new Dictionary<int,(int Root,int Index)>();bool unexpected=false;
        int limit=checked(baseline.Count+sources.Sum(s=>s.Length)*offsets.Length);
        foreach(var item in ShapeTraversal.Walk(target.Context,target.Context.Track(target.Page.Shapes),maxVisited:limit))
        {
            int id=item.Shape.StaticID;
            if(id<=0||!seen.Add(id))throw new InvalidDataException("Не подтверждена уникальность StaticID страницы.");
            if(!baseline.Contains(id))target.Context.RecordKnownChanges([], [id], []);
            int rootIndex,parentIndex;
            if(roots.TryGetValue(id,out rootIndex))
            {
                if(item.ParentId is not null)throw new InvalidDataException("Новая копия не является root страницы.");
                result[rootIndex]=item.Shape;parentIndex=-1;
            }
            else if(item.ParentId is { } parent&&owners.TryGetValue(parent,out var owner)) {rootIndex=owner.Root;parentIndex=owner.Index;}
            else {if(!baseline.Contains(id))unexpected=true;continue;}
            if(baseline.Contains(id))throw new InvalidDataException("Копия содержит ранее существовавший StaticID.");
            int index=counts[rootIndex]++;owners.Add(id,(rootIndex,index));
            var tree=sources[rootIndex%sources.Length];
            if(index>=tree.Length)throw new InvalidDataException("Превышен ожидаемый размер дерева копии.");
            var source=tree[index];
            int type=(int)item.Shape.Type;
            if(source.Type!=type||source.ParentIndex!=parentIndex||source.LayerIdentity!=ComScope.Identity(target.Context.Track(item.Shape.Layer)))
                throw new InvalidDataException("Не подтверждены тип, слой или структура дерева копии.");
            var b=Geometry(target,item.Shape);var offset=offsets[rootIndex/sources.Length];
            if(!Near(b.LeftMm,source.Bounds.LeftMm+offset.XMm)||!Near(b.BottomMm,source.Bounds.BottomMm+offset.YMm)
                ||!Near(b.WidthMm,source.Bounds.WidthMm)||!Near(b.HeightMm,source.Bounds.HeightMm)||!Near(item.Shape.RotationAngle,source.Rotation))
                throw new InvalidDataException("Не подтверждена геометрия элемента копии.");
            if(Content(target,item.Shape,type)!=source.Content)throw new InvalidDataException("Не подтверждены текст, шрифт или оформление элемента копии.");
        }
        if(unexpected||!baseline.IsSubsetOf(seen)||result.Any(shape=>shape is null)||counts.Where((count,i)=>count!=sources[i%sources.Length].Length).Any())
            throw new InvalidDataException("Не подтверждена полнота деревьев копий или сохранность исходных ID.");
        if(reply.KnownBeforeStaticIds.Length!=0||!reply.AffectedStaticIds.SequenceEqual(ids))throw new InvalidDataException("Affected ID не совпали с проверенными root копиями.");
        return result;
    }
    private static BoundsMm Geometry(DrawingTarget target,VGCore.Shape shape)
    {
        shape.GetBoundingBox(out var x,out var y,out var width,out var height,false);var p=target.Point(x,y);
        return new(p.XMm,p.YMm,width/target.U(1),height/target.U(1));
    }
    private static string Content(DrawingTarget target,VGCore.Shape shape,int type)
    {
        if(type==7)return "";
        var c=target.Context;var fill=c.Track(shape.Fill);var outline=c.Track(shape.Outline);
        int fillType=(int)fill.Type,outlineType=(int)outline.Type;
        var info=Dto.Of(("fill_type",fillType),("outline_type",outlineType),("outline_width_mm",outline.Width/target.U(1)));
        if(fillType==1)info["fill_color"]=Styles.ColorInfo(c,c.Track(fill.UniformColor));
        if(outlineType!=0)info["outline_color"]=Styles.ColorInfo(c,c.Track(outline.Color));
        if(type==6)
        {
            var story=c.Track(c.Track(shape.Text).Story);
            info["text"]=story.WideText;info["font_family"]=story.Font;info["font_size_pt"]=story.Size;
            info["bold"]=story.Bold;info["italic"]=story.Italic;info["alignment"]=(int)story.Alignment;
            info["line_spacing"]=story.LineSpacing;info["line_spacing_type"]=(int)story.LineSpacingType;
        }
        return System.Text.Json.JsonSerializer.Serialize(info,Protocol.JsonOptions);
    }
    private static bool Near(double actual,double expected)=>double.IsFinite(actual)&&double.IsFinite(expected)&&Math.Abs(actual-expected)<=.001;
}
