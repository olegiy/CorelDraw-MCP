namespace CorelMcp.Worker.Operations;

internal sealed record GeometrySegmentProof(int Type,double[] Coordinates);
internal sealed record GeometryShapeProof(int StaticId,int Type,double[] Bounds,double Rotation,GeometryShapeProof[] Children,GeometrySegmentProof[] Segments);
internal static class GeometryIsolation
{
    public static VGCore.Document Clone(OperationContext context, VGCore.Document sourceDocument, IReadOnlyList<VGCore.Shape> sources)
    {
        var range = context.Track(context.App.CreateShapeRange());
        foreach (var shape in sources) range.Add(shape);
        var sourceUnit=sourceDocument.Unit;
        var clone = context.Track(range.CreateDocumentFrom(false));
        clone.Unit = sourceUnit;
        return clone;
    }
    public static GeometryShapeProof[] Capture(OperationContext context,IReadOnlyList<VGCore.Shape> shapes)
    {
        int visited=0,segments=0;
        return shapes.Select(shape=>Read(shape,0)).ToArray();
        GeometryShapeProof Read(VGCore.Shape shape,int depth)
        {
            if(depth>32||++visited>50000)throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED","Превышен предел снимка структуры геометрии.");
            int id=shape.StaticID,type=(int)shape.Type;double[] bounds=[shape.LeftX,shape.BottomY,shape.SizeWidth,shape.SizeHeight];double rotation=shape.RotationAngle;
            GeometryShapeProof[] children=[];List<GeometrySegmentProof> curve=[];
            if(type==7)
            {
                var collection=context.Track(shape.Shapes);children=new GeometryShapeProof[collection.Count];
                for(int i=0;i<children.Length;i++)children[i]=Read(context.Track(collection[i+1]),depth+1);
            }
            if(type==3)
            {
                var collection=context.Track(context.Track(shape.Curve).Segments);
                for(int i=1;i<=collection.Count;i++)
                {
                    if(++segments>200000)throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED","Превышен предел 200000 сегментов снимка.");
                    var segment=context.Track(collection[i]);var start=context.Track(segment.StartNode);var end=context.Track(segment.EndNode);
                    List<double> coordinates=[start.PositionX,start.PositionY,end.PositionX,end.PositionY];
                    int kind=(int)segment.Type;
                    if(segment.Type==VGCore.cdrSegmentType.cdrCurveSegment)coordinates.AddRange([segment.StartingControlPointX,segment.StartingControlPointY,segment.EndingControlPointX,segment.EndingControlPointY]);
                    curve.Add(new(kind,coordinates.ToArray()));
                }
            }
            return new(id,type,bounds,rotation,children,curve.ToArray());
        }
    }
    // The source side is captured before CreateDocumentFrom; no source RCW getter runs after cloning.
    public static void Verify(GeometryShapeProof source,GeometryShapeProof copy,double mm,int depth=0)
    {
        if(depth>32||source.Type!=copy.Type||source.StaticId!=copy.StaticId||source.Bounds.Length!=4||copy.Bounds.Length!=4
            ||source.Children.Length!=copy.Children.Length||source.Segments.Length!=copy.Segments.Length)Mismatch();
        Values(source.Bounds,copy.Bounds);
        if(!double.IsFinite(source.Rotation-copy.Rotation)||Math.Abs(source.Rotation-copy.Rotation)>0.0000001)Mismatch();
        for(int i=0;i<source.Children.Length;i++)Verify(source.Children[i],copy.Children[i],mm,depth+1);
        for(int i=0;i<source.Segments.Length;i++)
        {
            if(source.Segments[i].Type!=copy.Segments[i].Type)Mismatch();Values(source.Segments[i].Coordinates,copy.Segments[i].Coordinates);
        }
        void Values(double[] a,double[] b)
        {if(a.Length!=b.Length)Mismatch();for(int i=0;i<a.Length;i++)if(!double.IsFinite((a[i]-b[i])*mm)||Math.Abs((a[i]-b[i])*mm)>0.0000001)Mismatch();}
    }
    private static void Mismatch() => throw new OperationFailure("COPY_GEOMETRY_MISMATCH", "Изолированная копия изменила StaticID, структуру или физическую геометрию.");
    public static void ConvertText(OperationContext context, VGCore.Shape shape, int depth = 0)
    {
        if (depth > 32) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Слишком глубокая группа при подготовке экспорта.");
        if (shape.Type == VGCore.cdrShapeType.cdrTextShape) { shape.ConvertToCurves(); return; }
        if (shape.Type != VGCore.cdrShapeType.cdrGroupShape) return;
        var children = context.Track(shape.Shapes);
        var items = Enumerable.Range(1, children.Count).Select(index => context.Track(children[index])).ToArray();
        foreach (var child in items) ConvertText(context, child, depth + 1);
    }
}
