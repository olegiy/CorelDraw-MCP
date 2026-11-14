namespace CorelMcp.Worker.Operations;

internal static class DrawingOperation
{
    public static object Execute(OperationContext context, DrawingArguments args)
    {
        context.Stage("drawing_preflight");
        var target = new DrawingTarget(context, args.PageIndex);
        Action execute = Hybrid.NativeExisting.TryPrepare(target,args.Command) ?? (args.Command switch
        {
            Hybrid.CreateBatchCommand or Hybrid.UpdateBatchCommand => Hybrid.NativeDrawing.Prepare(target,args.Command),
            CreateShapesCommand create => Shapes.PrepareCreate(target, create),
            TransformCommand transform => Shapes.PrepareTransform(target, transform),
            DuplicateCommand duplicate => Hybrid.NativeDuplicate.TryPrepare(target, duplicate) ?? Shapes.PrepareDuplicate(target, duplicate),
            StyleCommand style => Styles.Prepare(target, style),
            LayerCommand layer => Layers.Prepare(target, layer),
            GroupCommand group => Shapes.PrepareGroup(target, group),
            BooleanCommand boolean => Shapes.PrepareBoolean(target, boolean),
            CreateTextCommand text => Text.PrepareCreate(target, text),
            ConvertCommand convert => Hybrid.NativeText.TryPrepareConvert(target, convert) ?? Shapes.PrepareConvert(target, convert),
            CreateCurveCommand create => Curves.PrepareCreate(target, create),
            EditCurveCommand edit => Curves.PrepareEdit(target, edit),
            EditTextCommand edit => Hybrid.NativeText.TryPrepare(target,edit) ?? Text.PrepareEdit(target, edit),
            AlignCommand align => Layout.PrepareAlign(target, align),
            GridCommand grid => Layout.PrepareGrid(target, grid),
            LayoutCommand layout => Layout.PrepareShelf(target, layout),
            ImportCommand import => Import.Prepare(target, import),
            PageCommand pages => Pages.Prepare(target, pages),
            ClipCommand clip => PowerClip.Prepare(target, clip),
            _ => throw new OperationFailure("UNKNOWN_TOOL", "Неподдерживаемая команда рисования.")
        });
        if (args.Command is LayoutCommand { DryRun: true }) { execute(); return context.DrawingResult(false); }
        // Preflight has completed for every input. Opening an undo group is not a transaction.
        bool dirtyBefore = target.Document.Dirty;
        target.Document.Activate(); target.Page.Activate();
        CommandGroup.Run(() => { context.Mutating(); target.Document.BeginCommandGroup("CorelMCP " + context.Request.Tool); },
            () =>
            {
                if (target.Document.Dirty != dirtyBefore)
                {
                    context.RecordMutation([], []);
                    context.DrawingDetails["document_dirty_changed"] = true;
                }
                context.Stage("drawing"); execute();
            }, target.Document.EndCommandGroup, context.Cleanup);
        return context.DrawingResult(false);
    }
}

internal sealed class DrawingTarget
{
    public OperationContext Context { get; }
    public VGCore.Document Document => Context.Target!.Document;
    public VGCore.Page Page { get; }
    private readonly double unitPerMm, left, bottom;
    public DrawingTarget(OperationContext context, int pageIndex)
    {
        Context = context; Page = context.Registry.Page(context.Target!, context.Scope, pageIndex);
        unitPerMm = context.App.ConvertUnits(1, VGCore.cdrUnit.cdrMillimeter, Document.Unit);
        left = Page.LeftX; bottom = Page.BottomY;
        if (!double.IsFinite(unitPerMm) || unitPerMm <= 0 || !double.IsFinite(left) || !double.IsFinite(bottom))
            throw new OperationFailure("INVALID_UNITS", "Не определены координаты страницы и единицы документа.");
    }
    public double U(double mm) => mm * unitPerMm;
    public double X(double mm) => left + U(mm);
    public double Y(double mm) => bottom + U(mm);
    public PointMm Point(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new OperationFailure("INVALID_GEOMETRY", "Нечисловые координаты геометрии.");
        return new((x - left) / unitPerMm, (y - bottom) / unitPerMm);
    }
    public VGCore.Layer Layer(string id, bool requireEditable = true)
    {
        var layer = Context.Registry.ResolveLayer(Context.Target!, Page, Context.Scope, id);
        if (layer.IsSpecialLayer) throw new OperationFailure("UNSUPPORTED_LAYER", "Специальный слой не поддерживается этой операцией.");
        if (requireEditable && !layer.Editable) throw new OperationFailure("LOCKED_TARGET", "Целевой слой заблокирован.");
        return layer;
    }
    public VGCore.Shape[] Resolve(int[] ids, bool groups = true, bool bitmap = false)
    {
        var all = ShapeTraversal.Walk(Context, Context.Track(Page.Shapes)).ToArray();
        var map = all.ToDictionary(item => item.Shape.StaticID);
        var wanted = ids.ToHashSet();
        foreach (var id in ids)
        {
            if (!map.TryGetValue(id, out var item)) throw new OperationFailure("SHAPE_NOT_FOUND", "StaticID отсутствует на заданной странице: " + id);
            var parent = item.ParentId;
            while (parent is not null)
            {
                if (wanted.Contains(parent.Value)) throw new OperationFailure("OVERLAPPING_TARGETS", "Нельзя одновременно изменять группу и её потомка.");
                var ancestor = map[parent.Value];
                if (ancestor.Shape.Locked) throw new OperationFailure("LOCKED_TARGET", "Родительская группа заблокирована.");
                parent = ancestor.ParentId;
            }
            Validate(item.Shape, groups, bitmap);
            if (item.Shape.Type == VGCore.cdrShapeType.cdrGroupShape)
                foreach (var child in ShapeTraversal.Walk(Context, Context.Track(item.Shape.Shapes))) Validate(child.Shape, true, bitmap);
        }
        return ids.Select(id => map[id].Shape).ToArray();
    }
    private void Validate(VGCore.Shape shape, bool groups, bool bitmap)
    {
        var type = (int)shape.Type;
        if ((type is not (1 or 2 or 3 or 4 or 6 or 7) && !(bitmap && type == 5)) || (!groups && type == 7))
            throw new OperationFailure("UNSUPPORTED_SHAPE", "Тип фигуры не поддерживается этой операцией: " + shape.StaticID);
        if (shape.Locked || !Context.Track(shape.Layer).Editable) throw new OperationFailure("LOCKED_TARGET", "Фигура или её слой заблокированы: " + shape.StaticID);
        if (Context.Track(shape.Layer).IsSpecialLayer || Context.Track(shape.Effects).Count > 0)
            throw new OperationFailure("UNSUPPORTED_SHAPE", "Специальный слой или live effects не поддерживаются.");
        var clip = shape.PowerClip;
        if (clip is not null) { Context.Track(clip); throw new OperationFailure("UNSUPPORTED_SHAPE", "Для PowerClip используйте отдельную операцию."); }
    }
    public int[] TreeIds(VGCore.Shape shape) => new[] { shape.StaticID }.Concat(shape.Type == VGCore.cdrShapeType.cdrGroupShape
        ? ShapeTraversal.Walk(Context, Context.Track(shape.Shapes)).Select(item => item.Shape.StaticID) : []).ToArray();
    public void Observe(IEnumerable<int> before, params VGCore.Shape[] after)
    {
        var actual = after.SelectMany(TreeIds).Distinct().ToArray();
        Context.RecordMutation(before, actual);
        foreach (var shape in after)
        {
            var info = Info(shape);
            int index = Context.DrawingItems.FindIndex(item => item is Dictionary<string, object?> previous && previous.GetValueOrDefault("static_id") is int id && id == shape.StaticID);
            if (index < 0) Context.DrawingItems.Add(info); else Context.DrawingItems[index] = info;
        }
        Context.Stage("mutation_observed");
    }
    public Dictionary<string, object?> Info(VGCore.Shape shape)
    {
        var layer = Context.Track(shape.Layer); var outline = Context.Track(shape.Outline); var fill = Context.Track(shape.Fill);
        var value = Dto.Of(("static_id", shape.StaticID), ("type", (int)shape.Type),
            ("layer_id", Context.Registry.LayerId(Context.Target!, Page, layer)),
            ("bounds_mm", Inspection.Bounds(shape.LeftX - left, shape.BottomY - bottom, shape.SizeWidth, shape.SizeHeight, 1 / unitPerMm)),
            ("rotation_deg", shape.RotationAngle), ("fill_type", (int)fill.Type), ("outline_type", (int)outline.Type), ("outline_width_mm", outline.Width / unitPerMm));
        if ((int)fill.Type == 1) value["fill_color"] = Styles.ColorInfo(Context, Context.Track(fill.UniformColor));
        if ((int)outline.Type != 0) value["outline_color"] = Styles.ColorInfo(Context, Context.Track(outline.Color));
        if (shape.Type == VGCore.cdrShapeType.cdrTextShape)
        {
            var story = Context.Track(Context.Track(shape.Text).Story);
            value["text"] = story.WideText; value["font_family"] = story.Font; value["font_size_pt"] = story.Size;
        }
        return value;
    }
}
