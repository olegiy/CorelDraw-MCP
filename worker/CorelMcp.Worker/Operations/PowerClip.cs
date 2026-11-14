namespace CorelMcp.Worker.Operations;

internal static class PowerClip
{
    public static Action Prepare(DrawingTarget target, ClipCommand args)
    {
        var c = target.Context;
        var container = ShapeTraversal.Resolve(c, target.Page, [args.ContainerId])[0];
        if ((int)container.Type is not (1 or 2 or 3 or 4))
            throw new OperationFailure("INVALID_POWERCLIP_CONTAINER", "Контейнер должен быть замкнутой векторной фигурой.");
        var layer = c.Track(container.Layer);
        if (container.Locked || !layer.Editable) throw new OperationFailure("LOCKED_TARGET", "Контейнер или его слой заблокирован.");
        if (layer.IsSpecialLayer || c.Track(container.Effects).Count > 0)
            throw new OperationFailure("UNSUPPORTED_SHAPE", "Специальный слой или контейнер с live effects не поддерживается.");
        CheckAncestors(container, []);
        if (container.Type == VGCore.cdrShapeType.cdrCurveShape)
        {
            var paths = c.Track(c.Track(container.Curve).SubPaths);
            if (paths.Count == 0 || Enumerable.Range(1, paths.Count).Any(index => !c.Track(paths[index]).Closed))
                throw new OperationFailure("INVALID_POWERCLIP_CONTAINER", "Открытая кривая не принимается как контейнер.");
        }
        if (args is PlaceClipCommand place)
        {
            var shapes = ShapeTraversal.Resolve(c, target.Page, place.StaticIds);
            var selected = place.StaticIds.ToHashSet();
            foreach (var shape in shapes) CheckAncestors(shape, selected);
            var members = Members(shapes, true);
            if (members.Any(shape => shape.StaticID == container.StaticID))
                throw new OperationFailure("POWERCLIP_CYCLE", "Контейнер нельзя поместить в самого себя или в своего потомка.");
            var oldContents = Contents(container);
            var before = members.Concat(Members(oldContents, false)).Select(shape => shape.StaticID).Append(container.StaticID).Distinct().ToArray();
            var sizes = shapes.ToDictionary(shape => shape.StaticID, shape => (shape.SizeWidth, shape.SizeHeight));
            var range = c.Track(c.App.CreateShapeRange());
            foreach (var shape in shapes) range.Add(shape);
            return () =>
            {
                c.Mutating();
                range.AddToPowerClip(container, place.Center ? VGCore.cdrTriState.cdrTrue : VGCore.cdrTriState.cdrFalse);
                var current = Contents(container);
                c.RecordMutation(before, Members(current, false).Select(shape => shape.StaticID).Append(container.StaticID));
                c.DrawingDetails["container"] = target.Info(container);
                c.DrawingDetails["contents"] = shapes.Select(target.Info).ToArray();
                c.DrawingDetails["container_content_static_ids"] = current.Select(shape => shape.StaticID).ToArray();
                c.Stage("mutation_observed");
                if (shapes.Any(shape => Math.Abs(shape.SizeWidth - sizes[shape.StaticID].SizeWidth) > target.U(0.0002) || Math.Abs(shape.SizeHeight - sizes[shape.StaticID].SizeHeight) > target.U(0.0002)))
                    throw new OperationFailure("POWERCLIP_UNEXPECTED_SCALE", "Размеры содержимого изменились при помещении в PowerClip.", true);
            };
        }
        var clip = container.PowerClip;
        if (clip is null) throw new OperationFailure("NOT_A_POWERCLIP", "У точного контейнера нет содержимого PowerClip.");
        c.Track(clip);
        var roots = Contents(container);
        var ids = Members(roots, true).Select(shape => shape.StaticID).Append(container.StaticID).ToArray();
        return () =>
        {
            c.Mutating();
            var extracted = c.Track(clip.ExtractShapes());
            var shapes = Enumerable.Range(1, extracted.Count).Select(index => c.Track(extracted[index])).ToArray();
            c.RecordMutation(ids, Members(shapes, false).Select(shape => shape.StaticID).Append(container.StaticID));
            c.DrawingDetails["container"] = target.Info(container);
            c.DrawingDetails["contents"] = shapes.Select(target.Info).ToArray();
            c.Stage("mutation_observed");
        };

        VGCore.Shape[] Contents(VGCore.Shape shape)
        {
            var power = shape.PowerClip;
            if (power is null) return [];
            var children = c.Track(c.Track(power).Shapes);
            return Enumerable.Range(1, children.Count).Select(index => c.Track(children[index])).ToArray();
        }
        VGCore.Shape[] Members(IEnumerable<VGCore.Shape> roots, bool validate)
        {
            List<VGCore.Shape> result = [];
            HashSet<int> seen = [];
            foreach (var root in roots) Visit(root, 0);
            return result.ToArray();
            void Visit(VGCore.Shape shape, int depth)
            {
                if (depth > 32 || seen.Count >= 50000) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышен предел обхода PowerClip.");
                if (!seen.Add(shape.StaticID)) throw new OperationFailure("OVERLAPPING_TARGETS", "Нельзя одновременно перемещать родителя и его потомка.");
                if (validate)
                {
                    if ((int)shape.Type is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || c.Track(shape.Effects).Count > 0)
                        throw new OperationFailure("UNSUPPORTED_SHAPE", "Тип содержимого или live effects не поддерживаются.");
                    var sourceLayer = c.Track(shape.Layer);
                    if (shape.Locked || !sourceLayer.Editable) throw new OperationFailure("LOCKED_TARGET", "Содержимое PowerClip или его слой заблокирован.");
                    if (depth == 0 && sourceLayer.IsSpecialLayer) throw new OperationFailure("UNSUPPORTED_LAYER", "Специальный слой не поддерживается.");
                }
                result.Add(shape);
                if (shape.Type == VGCore.cdrShapeType.cdrGroupShape)
                {
                    var children = c.Track(shape.Shapes);
                    for (int index = 1; index <= children.Count; index++) Visit(c.Track(children[index]), depth + 1);
                }
                foreach (var child in Contents(shape)) Visit(child, depth + 1);
            }
        }
        void CheckAncestors(VGCore.Shape shape, HashSet<int> selected)
        {
            var ancestor = shape.ParentGroup;
            while (ancestor is not null)
            {
                var group = c.Track(ancestor);
                if (group.Locked) throw new OperationFailure("LOCKED_TARGET", "Родительская группа заблокирована.");
                if (selected.Contains(group.StaticID)) throw new OperationFailure("OVERLAPPING_TARGETS", "Группа и её потомок не принимаются вместе.");
                ancestor = group.ParentGroup;
            }
        }
    }
}
