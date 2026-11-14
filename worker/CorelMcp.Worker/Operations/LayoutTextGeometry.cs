using System.Security.Cryptography;

namespace CorelMcp.Worker.Operations;

internal static class LayoutTextGeometry
{
    // DisplayCurve is null for text in CorelDRAW 27. Read glyph geometry only in an owned, verified copy.
    public static Dictionary<int,string> Snapshot(DrawingTarget target,VGCore.Shape[] roots)
    {
        var c=target.Context;
        var texts=roots.SelectMany(root=>new[]{root}.Concat(root.Type==VGCore.cdrShapeType.cdrGroupShape
            ? ShapeTraversal.Walk(c,c.Track(root.Shapes)).Select(item=>item.Shape):[])).Where(shape=>shape.Type==VGCore.cdrShapeType.cdrTextShape).ToArray();
        Dictionary<int,string> result=[]; if(texts.Length==0) return result;
        VGCore.Document? temporary=null; int nodes=0,segments=0;
        var proofs=GeometryIsolation.Capture(c,roots);var rootIds=proofs.Select(p=>p.StaticId).ToArray();var textIds=texts.Select(t=>t.StaticID).ToArray();
        double originX=target.Document.DrawingOriginX,originY=target.Document.DrawingOriginY;
        double mm=c.App.ConvertUnits(1,target.Document.Unit,VGCore.cdrUnit.cdrMillimeter);
        using var privateContext=new OperationContext(c.Session,c.Request,_=>{},c.Cleanup);
        using var closeHold=c.Session.CreateScope();
        try
        {
            temporary=closeHold.Track(GeometryIsolation.Clone(privateContext,target.Document,roots));
            temporary.DrawingOriginX=originX; temporary.DrawingOriginY=originY;
            var page=privateContext.Track(temporary.ActivePage);
            var rootCopies=ShapeTraversal.Resolve(privateContext,page,rootIds);var copiedProofs=GeometryIsolation.Capture(privateContext,rootCopies);
            for(int i=0;i<proofs.Length;i++)GeometryIsolation.Verify(proofs[i],copiedProofs[i],mm);
            var copies=ShapeTraversal.Walk(privateContext,privateContext.Track(page.Shapes)).ToDictionary(item=>item.Shape.StaticID,item=>item.Shape);
            foreach(var textId in textIds)
            {
                var rendered=copies[textId]; rendered.ConvertToCurves();
                using var bytes=new MemoryStream(); using var bounded=new LimitedWriteStream(bytes,64*1024*1024,true,"LAYOUT_GEOMETRY_LIMIT"); using var writer=new BinaryWriter(bounded);
                Write(rendered,writer,0); writer.Flush();
                result[textId]=Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0,checked((int)bytes.Length))));
            }
        }
        finally
        {
            bool released=false;c.Cleanup.Try("layout.private_references",()=>{privateContext.Dispose();released=true;});
            if(temporary is not null&&released)c.Cleanup.Try("layout.temporary_text_document",()=>{temporary.Dirty=false;temporary.Close();});
        }
        if(!c.Cleanup.Completed) throw new OperationFailure("LAYOUT_PREFLIGHT_CLEANUP_FAILED","Закрытие временной копии для preflight не подтверждено.",null);
        return result;
        void Write(VGCore.Shape shape,BinaryWriter writer,int depth)
        {
            if(depth>32) throw new OperationFailure("LAYOUT_GEOMETRY_LIMIT","Превышена глубина геометрии текста.");
            writer.Write((int)shape.Type);
            if(shape.Type==VGCore.cdrShapeType.cdrGroupShape)
            {
                var children=privateContext.Track(shape.Shapes); writer.Write(children.Count);
                for(int i=1;i<=children.Count;i++) Write(privateContext.Track(children[i]),writer,depth+1);
                return;
            }
            if(shape.Type!=VGCore.cdrShapeType.cdrCurveShape) throw new OperationFailure("UNSUPPORTED_LAYOUT_GEOMETRY","Текст в изолированной копии не преобразован в кривые.");
            var paths=privateContext.Track(privateContext.Track(shape.Curve).SubPaths); writer.Write(paths.Count);
            for(int p=1;p<=paths.Count;p++)
            {
                var path=privateContext.Track(paths[p]); var ns=privateContext.Track(path.Nodes); var ss=privateContext.Track(path.Segments);
                nodes+=ns.Count;segments+=ss.Count;
                if(nodes>200000||segments>200000) throw new OperationFailure("LAYOUT_GEOMETRY_LIMIT","Геометрия текста превышает предел 200000 узлов/сегментов.");
                writer.Write(path.Closed);writer.Write(ns.Count);writer.Write(ss.Count);
                for(int n=1;n<=ns.Count;n++){var node=privateContext.Track(ns[n]);writer.Write(node.PositionX);writer.Write(node.PositionY);writer.Write((int)node.Type);}
                for(int s=1;s<=ss.Count;s++)
                {
                    var segment=privateContext.Track(ss[s]);writer.Write((int)segment.Type);
                    if(segment.Type==VGCore.cdrSegmentType.cdrCurveSegment){writer.Write(segment.StartingControlPointX);writer.Write(segment.StartingControlPointY);writer.Write(segment.EndingControlPointX);writer.Write(segment.EndingControlPointY);}
                }
            }
        }
    }
}
