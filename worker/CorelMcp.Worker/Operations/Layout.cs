using System.Security.Cryptography;
using System.Text.Json;

namespace CorelMcp.Worker.Operations;

internal static class Layout
{
    public static Action PrepareAlign(DrawingTarget target, AlignCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds, bitmap: true);
        var boxes = shapes.Select(shape => Box(target, shape, false, "")).ToArray();
        BoundsMm reference;
        if (args.Reference == "page") reference = new(0,0,Mm(target,target.Page.SizeWidth),Mm(target,target.Page.SizeHeight));
        else if (args.Reference == "selection") reference = Union(boxes);
        else reference = Bounds(target, ShapeTraversal.Resolve(c,target.Page,[args.ReferenceId!.Value])[0], false);
        var placements = LayoutPlanner.Align(new(args.Axis,args.Action == "align" ? args.Align! : "equal_gaps",boxes,reference));
        c.DrawingDetails["bounds_mode"] = "geometry"; c.DrawingDetails["placements"] = placements;
        List<Placement> actual = []; c.DrawingDetails["actual_bounds"] = actual;
        var map = shapes.ToDictionary(shape=>shape.StaticID);
        return () => { foreach(var placement in placements) Move(target,map[placement.StaticId],placement,false,actual); };
    }
    public static Action PrepareGrid(DrawingTarget target, GridCommand args)
    {
        var c=target.Context; var shapes=target.Resolve(args.StaticIds, bitmap: true);
        var boxes=shapes.Select(shape=>Box(target,shape,true,"")).ToArray(); var cell=Union(boxes);
        int count=shapes.Sum(shape=>target.TreeIds(shape).Length);
        var offsets=LayoutPlanner.Grid(new(cell,args.Rows,args.Columns,args.GapXMm,args.GapYMm,args.AnchorXMm,args.AnchorYMm,count));
        c.DrawingDetails["bounds_mode"]="outline"; c.DrawingDetails["cell_bounds"]=cell;
        List<Placement> actual=[]; c.DrawingDetails["actual_bounds"]=actual;
        return () =>
        {
            for(int index=0;index<shapes.Length;index++)
            {
                var b=boxes[index]; Move(target,shapes[index],new(b.StaticId,b.LeftMm+offsets[0].XMm,b.BottomMm+offsets[0].YMm,b.WidthMm,b.HeightMm),true,actual);
            }
            for(int cellIndex=1;cellIndex<offsets.Length;cellIndex++) foreach(var source in shapes)
            {
                c.Mutating(); var copy=c.Track(source.Duplicate(target.U(offsets[cellIndex].XMm-offsets[0].XMm),target.U(offsets[cellIndex].YMm-offsets[0].YMm)));
                c.RecordMutation([],target.TreeIds(copy)); var b=Bounds(target,copy,true);
                actual.Add(new(copy.StaticID,b.LeftMm,b.BottomMm,b.WidthMm,b.HeightMm)); target.Observe([],copy);
            }
        };
    }
    public static Action PrepareShelf(DrawingTarget target, LayoutCommand args)
    {
        var c=target.Context; var shapes=target.Resolve(args.StaticIds, bitmap: true); int nodes=0,segments=0;
        var textGeometry=LayoutTextGeometry.Snapshot(target,shapes);
        if(textGeometry.Count>0)shapes=target.Resolve(args.StaticIds,bitmap:true);
        var boxes=shapes.Select(shape=>Box(target,shape,true,Fingerprint(target,shape,textGeometry,ref nodes,ref segments))).ToArray();
        var input=new LayoutInput(Mm(target,target.Page.SizeWidth),Mm(target,target.Page.SizeHeight),args.MarginLeftMm,args.MarginRightMm,args.MarginTopMm,args.MarginBottomMm,args.GapXMm,args.GapYMm,boxes);
        var plan=LayoutPlanner.Shelf(input);
        c.DrawingDetails["dry_run"]=args.DryRun; c.DrawingDetails["bounds_mode"]="outline"; c.DrawingDetails["algorithm"]="shelf";
        c.DrawingDetails["placements"]=plan.Placements; c.DrawingDetails["unplaced"]=plan.Unplaced; c.DrawingDetails["layout_revision"]=plan.LayoutRevision;
        List<Placement> actual=[]; c.DrawingDetails["actual_bounds"]=actual;
        if(args.DryRun) return ()=>{};
        if(args.ExpectedRevision!=plan.LayoutRevision) throw new OperationFailure("STALE_LAYOUT","Геометрия, страница или параметры раскладки изменились после dry-run.");
        if(plan.Unplaced.Length>0) throw new OperationFailure("UNPLACED_ITEMS","Раскладка содержит unplaced; уменьшите набор явным отдельным запросом.");
        var map=shapes.ToDictionary(shape=>shape.StaticID);
        return ()=>{foreach(var placement in plan.Placements) Move(target,map[placement.StaticId],placement,true,actual);};
    }
    private static void Move(DrawingTarget target,VGCore.Shape shape,Placement placement,bool outline,List<Placement> actual)
    {
        var c=target.Context; var before=Bounds(target,shape,outline); var ids=target.TreeIds(shape);
        c.Mutating(); shape.Move(target.U(placement.XMm-before.LeftMm),target.U(placement.YMm-before.BottomMm)); c.RecordMutation(ids,ids);
        var after=Bounds(target,shape,outline); actual.Add(new(shape.StaticID,after.LeftMm,after.BottomMm,after.WidthMm,after.HeightMm));
        target.Observe(ids,shape);
        if(Math.Abs(after.LeftMm-placement.XMm)>0.001 || Math.Abs(after.BottomMm-placement.YMm)>0.001 || Math.Abs(after.WidthMm-placement.WidthMm)>0.001 || Math.Abs(after.HeightMm-placement.HeightMm)>0.001)
            throw new OperationFailure("PLACEMENT_MISMATCH","Наблюдаемые bounds отличаются от рассчитанного положения; автоматического отката нет.",true);
    }
    private static double Mm(DrawingTarget target,double value)=>target.Context.App.ConvertUnits(value,target.Document.Unit,VGCore.cdrUnit.cdrMillimeter);
    private static BoundsMm Bounds(DrawingTarget target,VGCore.Shape shape,bool outline)
    {
        try
        {
            shape.GetBoundingBox(out var x,out var y,out var w,out var h,outline); var point=target.Point(x,y); double width=Mm(target,w),height=Mm(target,h);
            if(!double.IsFinite(point.XMm)||!double.IsFinite(point.YMm)||!double.IsFinite(width)||!double.IsFinite(height)||width<0||height<0)
                throw new OperationFailure("UNSUPPORTED_LAYOUT_BOUNDS","Не удалось измерить конечные bounds.");
            if((width==0&&Mm(target,shape.SizeWidth)>0.000000001)||(height==0&&Mm(target,shape.SizeHeight)>0.000000001))
                throw new OperationFailure("UNSUPPORTED_LAYOUT_BOUNDS","CorelDRAW вернул нулевые bounds для ненулевой геометрии; флаги исходного слоя не изменены.");
            return new(point.XMm,point.YMm,width,height);
        }
        catch(System.Runtime.InteropServices.COMException) { throw new OperationFailure("UNSUPPORTED_LAYOUT_BOUNDS","CorelDRAW не подтвердил bounds с заданным режимом обводки."); }
    }
    private static LayoutBox Box(DrawingTarget target,VGCore.Shape shape,bool outline,string revision)
    {
        var b=Bounds(target,shape,outline); return new(shape.StaticID,b.LeftMm,b.BottomMm,b.WidthMm,b.HeightMm,revision);
    }
    private static BoundsMm Union(LayoutBox[] boxes)
    {
        double x=boxes.Min(b=>b.LeftMm),y=boxes.Min(b=>b.BottomMm);
        return new(x,y,boxes.Max(b=>b.LeftMm+b.WidthMm)-x,boxes.Max(b=>b.BottomMm+b.HeightMm)-y);
    }
    private static string Fingerprint(DrawingTarget target,VGCore.Shape root,IReadOnlyDictionary<int,string> textGeometry,ref int totalNodes,ref int totalSegments)
    {
        var c=target.Context; using var bytes=new MemoryStream();
        using var limited=new LimitedWriteStream(bytes,64*1024*1024,true,"LAYOUT_GEOMETRY_LIMIT"); using var writer=new BinaryWriter(limited);
        writer.Write(1); writer.Write((int)target.Document.Unit); writer.Write(target.Document.DrawingOriginX); writer.Write(target.Document.DrawingOriginY);
        writer.Write(target.Page.LeftX); writer.Write(target.Page.BottomY); writer.Write(c.Registry.PageId(c.Target!,target.Page));
        var members=new[]{new ShapeTraversal.Item(root,null,0)}.Concat(root.Type==VGCore.cdrShapeType.cdrGroupShape?ShapeTraversal.Walk(c,c.Track(root.Shapes)):[]);
        foreach(var item in members)
        {
            var shape=item.Shape; writer.Write(shape.StaticID); writer.Write(item.ParentId??0); writer.Write((int)shape.Type); writer.Write(shape.RotationAngle);
            shape.GetMatrix(out var a,out var b,out var d,out var e,out var tx,out var ty); foreach(var number in new[]{a,b,d,e,tx,ty}) writer.Write(number);
            var layer=c.Track(shape.Layer); writer.Write(c.Track(layer.TreeNode).Handle); WriteText(layer.Name); writer.Write(layer.Visible); writer.Write(layer.Printable);
            if(shape.Type==VGCore.cdrShapeType.cdrGroupShape) continue;
            var fill=c.Track(shape.Fill); var outline=c.Track(shape.Outline); writer.Write((int)fill.Type); writer.Write((int)outline.Type); writer.Write(outline.Width);
            if((int)fill.Type==1) writer.Write(JsonSerializer.SerializeToUtf8Bytes(Styles.ColorInfo(c,c.Track(fill.UniformColor)),Protocol.JsonOptions));
            if((int)outline.Type!=0) writer.Write(JsonSerializer.SerializeToUtf8Bytes(Styles.ColorInfo(c,c.Track(outline.Color)),Protocol.JsonOptions));
            if(shape.Type==VGCore.cdrShapeType.cdrTextShape)
            {
                WriteText(c.Track(c.Track(shape.Text).Story).WideText);writer.Write(textGeometry[shape.StaticID]);continue;
            }
            VGCore.Curve curve;
            try
            {
                if(shape.Type==VGCore.cdrShapeType.cdrBitmapShape)
                {
                    var bitmap=c.Track(shape.Bitmap);var cropped=bitmap.Cropped;writer.Write(cropped);
                    var crop=bitmap.CropEnvelope;
                    if(crop is null)
                    {
                        if(cropped)throw new OperationFailure("UNSUPPORTED_LAYOUT_GEOMETRY","Bitmap crop path отсутствует.");
                        // An uncropped bitmap is a rectangle. Preserve its effective frame and matrix.
                        var frame=Bounds(target,shape,true);writer.Write(frame.LeftMm);writer.Write(frame.BottomMm);writer.Write(frame.WidthMm);writer.Write(frame.HeightMm);continue;
                    }
                    curve=c.Track(crop);
                }
                else { var display=shape.DisplayCurve;if(display is null)throw new OperationFailure("UNSUPPORTED_LAYOUT_GEOMETRY","DisplayCurve отсутствует.");curve=c.Track(display); }
            }
            catch(System.Runtime.InteropServices.COMException) { throw new OperationFailure("UNSUPPORTED_LAYOUT_GEOMETRY","Полная DisplayCurve для revision недоступна; apply не разрешён."); }
            if(curve is null) throw new OperationFailure("UNSUPPORTED_LAYOUT_GEOMETRY","DisplayCurve отсутствует.");
            var paths=c.Track(curve.SubPaths); writer.Write(paths.Count);
            for(int p=1;p<=paths.Count;p++)
            {
                var path=c.Track(paths[p]); var nodes=c.Track(path.Nodes); var segments=c.Track(path.Segments);
                totalNodes+=nodes.Count; totalSegments+=segments.Count;
                if(totalNodes>200000||totalSegments>200000) throw new OperationFailure("LAYOUT_GEOMETRY_LIMIT","Полный revision превышает 200000 узлов/сегментов; раскладка не применена.");
                writer.Write(path.Closed); writer.Write(nodes.Count); writer.Write(segments.Count);
                for(int n=1;n<=nodes.Count;n++){var node=c.Track(nodes[n]); writer.Write(node.PositionX); writer.Write(node.PositionY); writer.Write((int)node.Type);}
                for(int s=1;s<=segments.Count;s++)
                {
                    var segment=c.Track(segments[s]); writer.Write((int)segment.Type);
                    if(segment.Type==VGCore.cdrSegmentType.cdrCurveSegment) { writer.Write(segment.StartingControlPointX); writer.Write(segment.StartingControlPointY); writer.Write(segment.EndingControlPointX); writer.Write(segment.EndingControlPointY); }
                }
            }
        }
        writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0,checked((int)bytes.Length))));
        void WriteText(string value) { writer.Write(value.Length); foreach(char character in value) writer.Write((ushort)character); }
    }
}
