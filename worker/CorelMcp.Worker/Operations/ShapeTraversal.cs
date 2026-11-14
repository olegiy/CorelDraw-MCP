namespace CorelMcp.Worker.Operations;

internal static class ShapeTraversal
{
    internal sealed record Item(VGCore.Shape Shape, int? ParentId, int Depth);
    public static IEnumerable<Item> Walk(OperationContext context, VGCore.Shapes shapes, int maxDepth = 32, int maxVisited = 50000)
    {
        int visited = 0;
        return Visit(shapes, null, 0);
        IEnumerable<Item> Visit(VGCore.Shapes collection, int? parent, int depth)
        {
            if (depth > maxDepth) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышена глубина обхода групп.");
            for (int index = 1; index <= collection.Count; index++)
            {
                if (++visited > maxVisited) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышен лимит обхода фигур.");
                var shape = context.Track(collection[index]);
                yield return new Item(shape, parent, depth);
                if (shape.Type == VGCore.cdrShapeType.cdrGroupShape)
                    foreach (var child in Visit(context.Track(shape.Shapes), shape.StaticID, depth + 1)) yield return child;
            }
        }
    }
    public static VGCore.Shape[] Resolve(OperationContext context, VGCore.Page page, int[] ids)
    {
        var wanted = ids.ToHashSet();
        Dictionary<int, VGCore.Shape> found = [];
        foreach (var item in Walk(context, context.Track(page.Shapes)))
            if (wanted.Contains(item.Shape.StaticID) && !found.TryAdd(item.Shape.StaticID, item.Shape))
                throw new OperationFailure("AMBIGUOUS_STATIC_ID", "StaticID не уникален на странице.");
        return ids.Select(id => found.GetValueOrDefault(id)
            ?? throw new OperationFailure("SHAPE_NOT_FOUND", "Не найден StaticID=" + id + " на заданной странице.")).ToArray();
    }
    public static VGCore.Shape[] ResolveExisting(OperationContext context, VGCore.Page page, int[] ids)
    {
        var wanted = ids.ToHashSet();
        return Walk(context, context.Track(page.Shapes)).Where(item => wanted.Contains(item.Shape.StaticID)).Select(item => item.Shape).ToArray();
    }
    public static int[] Selection(OperationContext context, VGCore.Document document)
    {
        var selection = context.Track(document.SelectionRange);
        var ids = new int[selection.Count];
        for (int index = 1; index <= selection.Count; index++) ids[index - 1] = context.Track(selection[index]).StaticID;
        return ids;
    }
}
