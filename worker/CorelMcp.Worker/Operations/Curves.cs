using System.Security.Cryptography;

namespace CorelMcp.Worker.Operations;

internal sealed record CurveNodeData(int SubpathIndex, int NodeIndex, int AbsoluteIndex, PointMm Position, string NodeType);
internal sealed record CurveSegmentData(int SubpathIndex, int SegmentIndex, string Kind, PointMm From, PointMm To, PointMm? Control1 = null, PointMm? Control2 = null);
internal sealed record CurvePathData(int Index, bool Closed, CurveNodeData[] Nodes, CurveSegmentData[] Segments);
internal sealed record CurveData(double[] Matrix, CurvePathData[] Paths)
{
    public string Revision()
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(1); foreach (var value in Matrix) writer.Write(value);
        writer.Write(Paths.Length);
        foreach (var path in Paths)
        {
            writer.Write(path.Index); writer.Write(path.Closed); writer.Write(path.Nodes.Length);
            foreach (var node in path.Nodes) { writer.Write(node.NodeIndex); writer.Write(node.NodeType); Point(node.Position); }
            writer.Write(path.Segments.Length);
            foreach (var segment in path.Segments)
            {
                writer.Write(segment.SegmentIndex); writer.Write(segment.Kind); Point(segment.From); Point(segment.To);
                if (segment.Control1 is not null) { Point(segment.Control1); Point(segment.Control2!); }
            }
        }
        writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        void Point(PointMm point) { writer.Write(point.XMm); writer.Write(point.YMm); }
    }
    public object Result(int staticId, int offset, int limit)
    {
        var nodes = Paths.SelectMany(path => path.Nodes).ToArray(); var segments = Paths.SelectMany(path => path.Segments).ToArray();
        int total = Math.Max(nodes.Length, segments.Length), end = Math.Min(total, offset + limit);
        return Dto.Of(("static_id", staticId), ("geometry_revision", Revision()),
            ("subpaths", Paths.Select(path => Dto.Of(("index", path.Index), ("closed", path.Closed), ("node_count", path.Nodes.Length), ("segment_count", path.Segments.Length))).ToArray()),
            ("nodes", nodes.Skip(offset).Take(limit).ToArray()), ("segments", segments.Skip(offset).Take(limit).ToArray()),
            ("total_nodes", nodes.Length), ("total_segments", segments.Length), ("next_offset", end < total ? end : null));
    }
}

internal static class Curves
{
    public static object Inspect(OperationContext c, InspectCurveArguments args)
    {
        c.Stage("inspecting_curve"); var target = new DrawingTarget(c, args.PageIndex);
        var shape = ShapeTraversal.Resolve(c, target.Page, [args.StaticId])[0];
        return Read(target, shape).Result(shape.StaticID, args.Offset, args.Limit);
    }
    internal static CurveData Read(DrawingTarget target, VGCore.Shape shape)
    {
        var c = target.Context;
        if (shape.Type != VGCore.cdrShapeType.cdrCurveShape) throw new OperationFailure("NOT_A_CURVE", "Требуется точная кривая; преобразование выполняется отдельным инструментом.");
        if (c.Track(shape.Effects).Count > 0) throw new OperationFailure("UNSUPPORTED_CURVE", "Кривая с live effects не поддерживается.");
        var curve = c.Track(shape.Curve); var paths = c.Track(curve.SubPaths);
        if (c.Track(curve.Nodes).Count > 10000 || c.Track(curve.Segments).Count > 10000 || paths.Count > 5000)
            throw new OperationFailure("CURVE_LIMIT_EXCEEDED", "Геометрия превышает 10000 узлов/сегментов или 5000 subpaths.");
        shape.GetMatrix(out var a, out var b, out var d, out var e, out var tx, out var ty);
        double[] matrix = [a, b, d, e, tx, ty];
        if (matrix.Any(value => !double.IsFinite(value))) throw new OperationFailure("INVALID_GEOMETRY", "Матрица содержит нечисловые значения.");
        List<CurvePathData> result = []; int absolute = 0;
        for (int p = 1; p <= paths.Count; p++)
        {
            var path = c.Track(paths[p]); var nodes = c.Track(path.Nodes); var segments = c.Track(path.Segments);
            List<CurveNodeData> nodeData = []; List<CurveSegmentData> segmentData = [];
            for (int n = 1; n <= nodes.Count; n++)
            {
                var node = c.Track(nodes[n]);
                nodeData.Add(new(p, n, ++absolute, target.Point(node.PositionX, node.PositionY), node.Type switch
                { VGCore.cdrNodeType.cdrCuspNode => "cusp", VGCore.cdrNodeType.cdrSmoothNode => "smooth", VGCore.cdrNodeType.cdrSymmetricalNode => "symmetrical", _ => "mixed" }));
            }
            for (int s = 1; s <= segments.Count; s++)
            {
                var segment = c.Track(segments[s]); var start = c.Track(segment.StartNode); var end = c.Track(segment.EndNode);
                var from = target.Point(start.PositionX, start.PositionY); var to = target.Point(end.PositionX, end.PositionY);
                if (segment.Type == VGCore.cdrSegmentType.cdrLineSegment) segmentData.Add(new(p, s, "line", from, to));
                else if (segment.Type == VGCore.cdrSegmentType.cdrCurveSegment)
                    segmentData.Add(new(p, s, "cubic", from, to, target.Point(segment.StartingControlPointX, segment.StartingControlPointY), target.Point(segment.EndingControlPointX, segment.EndingControlPointY)));
                else throw new OperationFailure("UNSUPPORTED_CURVE", "Неизвестный тип сегмента кривой.");
            }
            result.Add(new(p, path.Closed, nodeData.ToArray(), segmentData.ToArray()));
        }
        return new(matrix, result.ToArray());
    }
    public static Action PrepareCreate(DrawingTarget target, CreateCurveCommand args)
    {
        var c = target.Context; var layer = target.Layer(args.LayerId);
        return () =>
        {
            var curve = c.Track(c.App.CreateCurve(target.Document));
            foreach (var path in args.Subpaths)
            {
                var native = c.Track(curve.CreateSubPath(target.X(path.Start.XMm), target.Y(path.Start.YMm)));
                foreach (var segment in path.Segments)
                {
                    if (segment is CurveCubic cubic)
                        c.Track(native.AppendCurveSegment2(target.X(cubic.To.XMm), target.Y(cubic.To.YMm), target.X(cubic.Control1.XMm), target.Y(cubic.Control1.YMm), target.X(cubic.Control2.XMm), target.Y(cubic.Control2.YMm)));
                    else c.Track(native.AppendLineSegment(target.X(segment.To.XMm), target.Y(segment.To.YMm)));
                }
                native.Closed = path.Closed;
            }
            c.Mutating(); var shape = c.Track(layer.CreateCurve(curve));
            c.RecordMutation([], [shape.StaticID]);
            c.DrawingDetails["geometry_revision"] = Read(target, shape).Revision(); target.Observe([], shape);
        };
    }
    public static Action PrepareEdit(DrawingTarget target, EditCurveCommand args)
    {
        var c = target.Context; var shape = target.Resolve([args.StaticId], false)[0]; var current = Read(target, shape);
        if (current.Revision() != args.ExpectedRevision) throw new OperationFailure("STALE_GEOMETRY", "Геометрия изменилась после инспекции; правка не выполнена.");
        var curve = c.Track(shape.Curve); var paths = c.Track(curve.SubPaths);
        VGCore.SubPath PathAt(int index)
        {
            if (index < 1 || index > paths.Count) throw new OperationFailure("CURVE_INDEX_NOT_FOUND", "Индекс subpath отсутствует.");
            return c.Track(paths[index]);
        }
        VGCore.Node NodeAt(VGCore.SubPath path, int index)
        {
            var nodes = c.Track(path.Nodes);
            if (index < 1 || index > nodes.Count) throw new OperationFailure("CURVE_INDEX_NOT_FOUND", "Индекс узла отсутствует.");
            return c.Track(nodes[index]);
        }
        Action mutation;
        switch (args)
        {
            case MoveNodeCommand move:
                var moving = NodeAt(PathAt(move.SubpathIndex), move.NodeIndex); mutation = () => moving.SetPosition(target.X(move.XMm), target.Y(move.YMm)); break;
            case InsertNodeCommand insert:
                if (current.Paths.Sum(path => path.Nodes.Length) >= 10000) throw new OperationFailure("CURVE_LIMIT_EXCEEDED", "Вставка превысит 10000 узлов.");
                var segments = c.Track(PathAt(insert.SubpathIndex).Segments);
                if (insert.SegmentIndex > segments.Count) throw new OperationFailure("CURVE_INDEX_NOT_FOUND", "Индекс сегмента отсутствует.");
                var segment = c.Track(segments[insert.SegmentIndex]); mutation = () => c.Track(segment.AddNodeAt(insert.T, VGCore.cdrSegmentOffsetType.cdrParamSegmentOffset)); break;
            case DeleteNodeCommand delete:
                var deletingPath = PathAt(delete.SubpathIndex); var deleting = NodeAt(deletingPath, delete.NodeIndex);
                if (c.Track(deletingPath.Nodes).Count <= (deletingPath.Closed ? 3 : 2)) throw new OperationFailure("MINIMUM_NODES", "Нельзя удалить последний допустимый узел контура.");
                mutation = deleting.Delete; break;
            case SetNodeTypeCommand type:
                var typed = NodeAt(PathAt(type.SubpathIndex), type.NodeIndex);
                mutation = () => typed.Type = type.NodeType switch { "cusp" => VGCore.cdrNodeType.cdrCuspNode, "smooth" => VGCore.cdrNodeType.cdrSmoothNode, _ => VGCore.cdrNodeType.cdrSymmetricalNode }; break;
            case CloseSubpathCommand close:
                var closing = PathAt(close.SubpathIndex);
                if (c.Track(closing.Nodes).Count < 3) throw new OperationFailure("MINIMUM_NODES", "Для замыкания нужны минимум три узла.");
                mutation = () => closing.Closed = true; break;
            case JoinEndpointsCommand join:
                var firstPath = PathAt(join.FirstSubpathIndex); var secondPath = PathAt(join.SecondSubpathIndex);
                if (firstPath.Closed || secondPath.Closed) throw new OperationFailure("NOT_AN_ENDPOINT", "Соединение допускает только концы открытых subpaths.");
                if (join.FirstSubpathIndex == join.SecondSubpathIndex && c.Track(firstPath.Nodes).Count < 3) throw new OperationFailure("MINIMUM_NODES", "Для замыкания нужны минимум три узла.");
                var first = c.Track(join.FirstEnd == "start" ? firstPath.StartNode : firstPath.EndNode);
                var second = c.Track(join.SecondEnd == "start" ? secondPath.StartNode : secondPath.EndNode);
                var p = target.Point(first.PositionX, first.PositionY); var q = target.Point(second.PositionX, second.PositionY);
                if (Math.Sqrt(Math.Pow(p.XMm - q.XMm, 2) + Math.Pow(p.YMm - q.YMm, 2)) > join.ToleranceMm)
                    throw new OperationFailure("ENDPOINTS_TOO_FAR", "Расстояние между концами превышает заданный tolerance_mm.");
                mutation = () => first.JoinWith(second); break;
            default: throw new OperationFailure("INVALID_INPUT", "Неизвестная правка кривой.");
        }
        return () =>
        {
            c.Mutating(); mutation(); c.RecordMutation([shape.StaticID], [shape.StaticID]);
            var after = Read(target, shape); c.DrawingDetails["geometry_revision"] = after.Revision();
            c.DrawingDetails["total_nodes"] = after.Paths.Sum(path => path.Nodes.Length); target.Observe([shape.StaticID], shape);
        };
    }
}
