namespace CorelMcp.Worker.Operations;

internal static class Shapes
{
    public static Action PrepareCreate(DrawingTarget target, CreateShapesCommand args)
    {
        var c = target.Context; var layer = target.Layer(args.LayerId);
        return () =>
        {
            foreach (var item in args.Items)
            {
                c.Mutating();
                var shape = c.Track(item switch
                {
                    RectangleItem r => layer.CreateRectangle2(target.X(r.XMm), target.Y(r.YMm), target.U(r.WidthMm), target.U(r.HeightMm)),
                    EllipseItem e => layer.CreateEllipse2(target.X(e.XMm + e.WidthMm / 2), target.Y(e.YMm + e.HeightMm / 2), target.U(e.WidthMm / 2), target.U(e.HeightMm / 2)),
                    LineItem l => layer.CreateLineSegment(target.X(l.X1Mm), target.Y(l.Y1Mm), target.X(l.X2Mm), target.Y(l.Y2Mm)),
                    _ => throw new OperationFailure("INVALID_INPUT", "Неподдерживаемая фигура.")
                });
                target.Observe([], shape);
            }
        };
    }
    public static Action PrepareTransform(DrawingTarget target, TransformCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds, bitmap: true);
        return () =>
        {
            foreach (var shape in shapes)
            {
                var ids = target.TreeIds(shape); c.Mutating();
                switch (args)
                {
                    case MoveCommand m: shape.Move(target.U(m.DxMm), target.U(m.DyMm)); break;
                    case ScaleCommand s: shape.StretchEx(target.X(s.AnchorXMm), target.Y(s.AnchorYMm), s.Sx, s.Sy, true); break;
                    case RotateCommand r: shape.RotateEx(r.AngleDeg, target.X(r.AnchorXMm), target.Y(r.AnchorYMm)); break;
                    // Native StretchEx supports signed factors; the public scale contract stays positive.
                    case FlipCommand f: shape.StretchEx(target.X(f.AnchorXMm), target.Y(f.AnchorYMm), f.Axis == "horizontal" ? -1 : 1, f.Axis == "vertical" ? -1 : 1, true); break;
                }
                target.Observe(ids, shape);
            }
        };
    }
    public static Action PrepareDuplicate(DrawingTarget target, DuplicateCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds, bitmap: true);
        var count = shapes.Sum(shape => target.TreeIds(shape).Length);
        if ((long)count * args.Copies > 1000) throw new OperationFailure("RESULT_LIMIT_EXCEEDED", "Дублирование создаст более 1000 фигур с учётом содержимого групп.");
        return () =>
        {
            for (int copy = 1; copy <= args.Copies; copy++) foreach (var source in shapes)
            {
                c.Mutating(); var shape = c.Track(source.Duplicate(target.U(args.StepXMm * copy), target.U(args.StepYMm * copy)));
                target.Observe([], shape);
            }
        };
    }
    public static Action PrepareGroup(DrawingTarget target, GroupCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds, bitmap: true);
        var before = shapes.SelectMany(target.TreeIds).ToArray();
        if (args.Action == "ungroup")
        {
            if (shapes[0].Type != VGCore.cdrShapeType.cdrGroupShape) throw new OperationFailure("NOT_A_GROUP", "Ungroup требует ровно одну группу.");
            return () =>
            {
                c.Mutating(); var result = c.Track(shapes[0].UngroupEx());
                var children = Enumerable.Range(1, result.Count).Select(index => c.Track(result[index])).ToArray();
                target.Observe(before, children);
            };
        }
        var layerHandles = shapes.Select(shape => c.Track(c.Track(shape.Layer).TreeNode).Handle).Distinct().Count();
        var parents = shapes.Select(shape => { var parent = shape.ParentGroup; return parent is null ? 0 : c.Track(parent).StaticID; }).Distinct().Count();
        if (layerHandles != 1 || parents != 1) throw new OperationFailure("GROUP_TARGET_MISMATCH", "Группируемые фигуры должны иметь один слой и одного родителя.");
        var range = c.Track(c.App.CreateShapeRange()); foreach (var shape in shapes) range.Add(shape);
        return () => { c.Mutating(); var group = c.Track(range.Group()); target.Observe(before, group); };
    }
    public static Action PrepareBoolean(DrawingTarget target, BooleanCommand args)
    {
        var c = target.Context; var shapes = target.Resolve([args.LeftId, args.RightId], false);
        if (shapes.Any(shape => (int)shape.Type is not (1 or 2 or 3 or 4))) throw new OperationFailure("UNSUPPORTED_SHAPE", "Для булевой операции требуется простая геометрия; текст сначала преобразуйте в кривые.");
        if (shapes.Any(shape => shape.ParentGroup is not null)) throw new OperationFailure("UNSUPPORTED_SHAPE", "Булева операция внутри группы не поддерживается; явно разгруппируйте фигуры.");
        return () =>
        {
            c.Mutating();
            var result = c.Track(args.Action switch
            {
                "trim" => shapes[0].Trim(shapes[1], args.KeepLeft, args.KeepRight),
                "weld" => shapes[0].Weld(shapes[1], args.KeepLeft, args.KeepRight),
                "intersect" => shapes[0].Intersect(shapes[1], args.KeepLeft, args.KeepRight),
                _ => throw new OperationFailure("INVALID_INPUT", "Неподдерживаемая булева операция.")
            });
            // SDK can reuse a source ID. Compute created/deleted from observed page membership.
            var surviving = ShapeTraversal.ResolveExisting(c, target.Page, [args.LeftId, args.RightId]);
            var after = new[] { result }.Concat(surviving).DistinctBy(shape => shape.StaticID).ToArray();
            c.RecordMutation([args.LeftId, args.RightId], after.SelectMany(target.TreeIds));
            c.DrawingItems.Add(target.Info(result)); c.Stage("mutation_observed");
        };
    }
    public static Action PrepareConvert(DrawingTarget target, ConvertCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds);
        List<object> mappings = []; c.DrawingDetails["id_mappings"] = mappings;
        return () =>
        {
            foreach (var shape in shapes)
            {
                var before = target.TreeIds(shape); int sourceId = shape.StaticID;
                if (shape.Type == VGCore.cdrShapeType.cdrCurveShape)
                {
                    mappings.Add(Dto.Of(("source_static_id", sourceId), ("result_static_ids", before)));
                    c.DrawingItems.Add(target.Info(shape)); continue;
                }
                c.Mutating(); shape.ConvertToCurves();
                mappings.Add(Dto.Of(("source_static_id", sourceId), ("result_static_ids", target.TreeIds(shape))));
                target.Observe(before, shape);
            }
        };
    }
}
