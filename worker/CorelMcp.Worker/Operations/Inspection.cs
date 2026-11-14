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
            if((int)outline.Type!=0)details["outline_color"]=Styles.ColorInfo(context,context.Track(outline.Color));
            appearance=details;
        } catch { }
        var result = Dto.Of(("static_id", shape.StaticID), ("name", shape.Name), ("type", (int)shape.Type), ("layer", context.Track(shape.Layer).Name),
            ("selected", selected), ("depth", depth), ("parent_static_id", parent), ("bounds", Bounds(shape.LeftX, shape.BottomY, shape.SizeWidth, shape.SizeHeight, 1)),
            ("bounds_mm", Bounds(shape.LeftX, shape.BottomY, shape.SizeWidth, shape.SizeHeight, factor)), ("rotation", shape.RotationAngle), ("appearance", appearance));
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
}
