namespace CorelMcp.Worker.Operations;

internal static class Inspection
{
    public static object Execute(OperationContext context, InspectArguments args)
    {
        context.Stage("inspecting");
        var entry = context.Target!; var doc = entry.Document;
        var page = context.Registry.Page(entry, context.Scope, args.PageIndex);
        var factor = context.App.ConvertUnits(1, doc.Unit, VGCore.cdrUnit.cdrMillimeter);
        var selection = context.Track(doc.SelectionRange);
        var selectionIds = ShapeTraversal.Selection(context, doc);
        var selected = selectionIds.ToHashSet();
        List<object> pages = [], layers = [], shapes = [];
        var docPages = context.Track(doc.Pages); var pageLayers = context.Track(page.Layers);
        for (int index = 1; index <= Math.Min(docPages.Count, args.Limit); index++)
        {
            var item = context.Track(docPages[index]);
            pages.Add(Dto.Of(("index", item.Index), ("page_id", context.Registry.PageId(entry, item)), ("name", item.Name), ("width", item.SizeWidth), ("height", item.SizeHeight)));
        }
        for (int index = 1; index <= Math.Min(pageLayers.Count, args.Limit); index++)
        {
            var layer = context.Track(pageLayers[index]);
            layers.Add(Dto.Of(("layer_id", context.Registry.LayerId(entry, page, layer)), ("name", layer.Name), ("visible", layer.Visible),
                ("editable", layer.Editable), ("printable", layer.Printable), ("is_special_layer", layer.IsSpecialLayer), ("shapes", context.Track(layer.Shapes).Count)));
        }
        int visited = 0, matched = 0; bool depthTruncated = false, visitedTruncated = false;
        void Visit(VGCore.Shapes collection, int? parent, int depth)
        {
            for (int index = 1; index <= collection.Count; index++)
            {
                if (visited >= args.MaxVisited) { visitedTruncated = true; return; }
                visited++;
                var shape = context.Track(collection[index]);
                bool match = (args.NameFilter is null || (shape.Name ?? "").Contains(args.NameFilter, StringComparison.OrdinalIgnoreCase))
                    && (args.ShapeTypes is null || args.ShapeTypes.Contains((int)shape.Type)) && (args.StaticIds is null || args.StaticIds.Contains(shape.StaticID));
                if (match)
                {
                    if (matched >= args.Offset && shapes.Count < args.Limit) shapes.Add(ShapeInfo(context, shape, selected.Contains(shape.StaticID), parent, depth, factor, args));
                    matched++;
                }
                if (!args.Recursive || shape.Type != VGCore.cdrShapeType.cdrGroupShape) continue;
                var children = context.Track(shape.Shapes);
                if (depth >= args.MaxDepth) { if (children.Count > 0) depthTruncated = true; continue; }
                Visit(children, shape.StaticID, depth + 1);
            }
        }
        var topLevel = context.Track(page.Shapes); Visit(topLevel, null, 0);
        int end = Math.Min(matched, args.Offset + args.Limit);
        List<string> reasons = [];
        if (depthTruncated) reasons.Add("max_depth"); if (visitedTruncated) reasons.Add("max_visited"); if (end < matched) reasons.Add("limit");
        return Dto.Of(("document", context.Registry.Info(entry, context.Scope)), ("pages", pages), ("page_index", page.Index), ("page_id", context.Registry.PageId(entry, page)),
            ("layers", layers), ("selection_ids", selectionIds.Take(args.Limit).ToArray()), ("shapes", shapes), ("total_shapes", topLevel.Count),
            ("next_offset", end < matched ? end : null), ("total_pages", docPages.Count), ("total_layers", pageLayers.Count), ("total_selected", selection.Count),
            ("pages_truncated", docPages.Count > args.Limit), ("layers_truncated", pageLayers.Count > args.Limit), ("selection_truncated", selection.Count > args.Limit),
            ("visited_shapes", visited), ("matched_shapes", matched), ("depth_truncated", depthTruncated), ("visited_truncated", visitedTruncated),
            ("shapes_truncated", end < matched), ("truncation_reasons", reasons), ("reference_point", (int)doc.ReferencePoint),
            ("selection_bounds", selection.Count > 0 ? Bounds(selection.LeftX, selection.BottomY, selection.SizeWidth, selection.SizeHeight, 1) : null),
            ("selection_bounds_mm", selection.Count > 0 ? Bounds(selection.LeftX, selection.BottomY, selection.SizeWidth, selection.SizeHeight, factor) : null));
    }
    public static Dictionary<string, object?> Bounds(double x, double y, double width, double height, double factor)
        => Dto.Of(("left", x * factor), ("bottom", y * factor), ("width", width * factor), ("height", height * factor));
    private static object ShapeInfo(OperationContext context, VGCore.Shape shape, bool selected, int? parent, int depth, double factor, InspectArguments args)
    {
        object? appearance = null;
        try
        {
            var fill=context.Track(shape.Fill);var outline=context.Track(shape.Outline);
            var details=Dto.Of(("fill_type",(int)fill.Type),("outline_type",(int)outline.Type),("outline_width_mm",outline.Width*factor));
            if((int)fill.Type==1)details["fill_color"]=Styles.ColorInfo(context,context.Track(fill.UniformColor));
            if((int)fill.Type==2)details["fountain"]=Styles.FountainInfo(context,context.Track(fill.Fountain));
            if((int)outline.Type!=0)details["outline_color"]=Styles.ColorInfo(context,context.Track(outline.Color));
            appearance=details;
        } catch { }
        var result = Dto.Of(("static_id", shape.StaticID), ("name", shape.Name), ("type", (int)shape.Type), ("layer", context.Track(shape.Layer).Name),
            ("selected", selected), ("depth", depth), ("parent_static_id", parent), ("bounds", Bounds(shape.LeftX, shape.BottomY, shape.SizeWidth, shape.SizeHeight, 1)),
            ("bounds_mm", Bounds(shape.LeftX, shape.BottomY, shape.SizeWidth, shape.SizeHeight, factor)), ("rotation", shape.RotationAngle), ("appearance", appearance),
            ("effects", EffectInfo(context, shape)));
        if (args.IncludeText)
        {
            string? text = null;
            if (shape.Type == VGCore.cdrShapeType.cdrTextShape) text = context.Track(context.Track(shape.Text).Story).WideText;
            bool truncated = text?.Length > args.TextLimit;
            if (truncated)
            {
                int limit = args.TextLimit;
                if (char.IsHighSurrogate(text![limit - 1])) limit--;
                text = text[..limit];
            }
            result["text"] = text; result["text_truncated"] = truncated;
        }
        return result;
    }
    // Reused by Operations/Transparency and Operations/DropShadow for the report read-back (observed state, not the request).
    internal static object EffectInfo(OperationContext context, VGCore.Shape shape)
    {
        int? count = null; List<object> items = [];
        try
        {
            var effects = context.Track(shape.Effects); count = effects.Count;
            for (int index = 1; index <= Math.Min(count.Value, 32); index++)
            {
                var effect = context.Track(effects[index]); var type = effect.Type;
                var item = Dto.Of(("type", (int)type), ("name", type.ToString()));
                if (type == VGCore.cdrEffectType.cdrLens)
                {
                    var lens = context.Track(effect.Lens);
                    item["lens"] = Dto.Of(("type", (int)lens.Type), ("name", lens.Type.ToString()), ("frozen", lens.Frozen), ("uses_viewpoint", lens.UseViewPoint));
                    var transparency = context.Track(shape.Transparency); var transparencyType = transparency.Type;
                    var properties = Dto.Of(("type", (int)transparencyType), ("name", transparencyType.ToString()));
                    if (transparencyType != VGCore.cdrTransparencyType.cdrNoTransparency)
                    {
                        properties["frozen"] = transparency.Frozen;
                        properties["merge_mode"] = (int)transparency.MergeMode;
                        properties["applied_to"] = (int)transparency.AppliedTo;
                    }
                    if (transparencyType == VGCore.cdrTransparencyType.cdrUniformTransparency) properties["uniform"] = transparency.Uniform;
                    item["transparency"] = properties;
                }
                if (type == VGCore.cdrEffectType.cdrDropShadow)
                {
                    // Observed read-back for the drop shadow; offsets are document lengths and are converted to mm.
                    // Live-verified defaults when v1 does not expose them: perspective_angle=0, perspective_stretch=0.
                    var shadow = context.Track(effect.DropShadow);
                    // The unit comes from the shape's own document (shape.TreeNode.Document), not context.Target:
                    // EffectInfo must stay usable without a resolved target and a null Target! would throw an NRE
                    // that the COMException-only catch below does not cover.
                    var factor = context.App.ConvertUnits(1, context.Track(shape.TreeNode).Document.Unit, VGCore.cdrUnit.cdrMillimeter);
                    item["drop_shadow"] = Dto.Of(("type", (int)shadow.Type), ("opacity", shadow.Opacity), ("feather", shadow.Feather),
                        ("offset_x_mm", shadow.OffsetX * factor), ("offset_y_mm", shadow.OffsetY * factor),
                        ("feather_type", (int)shadow.FeatherType), ("feather_edge", (int)shadow.FeatherEdge),
                        ("fade", shadow.Fade), ("merge_mode", (int)shadow.MergeMode),
                        ("perspective_angle", shadow.PerspectiveAngle), ("perspective_stretch", shadow.PerspectiveStretch));
                }
                items.Add(item);
            }
            return Dto.Of(("count", count), ("items", items), ("complete", count >= 0 && count == items.Count));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Dto.Of(("count", count), ("items", items), ("complete", false), ("reason", "effect_read_failed"));
        }
    }
}
