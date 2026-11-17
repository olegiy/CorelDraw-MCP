namespace CorelMcp.Worker.Operations;

// corel_set_transparency: typed dispatch over IVGTransparency (uniform/fountain/none) following Operations/PowerClip.cs —
// resolve + full target validation before the mutation closure; groups pass only when every descendant is supported,
// then the transparency is applied to each leaf (never to the group node, so existing group/move/duplicate semantics
// keep working). StaticID never changes; the report reuses Inspection.EffectInfo for the observed read-back.
internal static class Transparency
{
    public static Action Prepare(DrawingTarget target, TransparencyCommand args)
    {
        var c = target.Context;
        // Enum mappings are pure and resolved in preflight, like the fountain colors in Styles.PrepareFountain.
        VGCore.cdrTransparencyAppliedTo? appliedTo = args.Kind == "none" ? null : DrawingContracts.TransparencyAppliedTo(args.AppliedTo);
        VGCore.cdrMergeMode? mergeMode = args.Kind == "none" ? null : DrawingContracts.MergeMode(args.MergeMode);
        VGCore.cdrFountainFillType? fountainType = args.Kind == "fountain" ? DrawingContracts.FountainType(args.Type!) : null;
        var shapes = ShapeTraversal.Resolve(c, target.Page, args.StaticIds);
        var selected = args.StaticIds.ToHashSet();
        foreach (var root in shapes) CheckAncestors(c, root, selected);
        var leaves = Leaves(c, shapes);
        var observed = leaves.Select(shape => shape.StaticID).ToArray();
        return () =>
        {
            c.Mutating();
            foreach (var shape in leaves)
            {
                var transparency = c.Track(shape.Transparency);
                switch (args.Kind)
                {
                    case "none":
                        transparency.ApplyNoTransparency();
                        break;
                    case "uniform":
                        transparency.ApplyUniformTransparency(args.Value!.Value);
                        break;
                    default:
                        // CenterOffsetX/Y are percentages of the object in API 27: passed as-is, never target.U().
                        c.Track(transparency.ApplyFountainTransparency(args.Start!.Value, args.End!.Value, fountainType!.Value,
                            args.AngleDeg ?? 0, CenterOffsetX: args.CenterOffsetXPercent ?? 0, CenterOffsetY: args.CenterOffsetYPercent ?? 0));
                        break;
                }
                // Live-verified (COM error "У объекта отсутствует прозрачность" when set before): AppliedTo/MergeMode
                // exist only once a transparency lens is applied, so they are set after the apply and before Freeze.
                if (appliedTo is { } where) transparency.AppliedTo = where;
                if (mergeMode is { } mode) transparency.MergeMode = mode;
                if (args.Freeze is true) transparency.Freeze();
                c.DrawingItems.Add(Dto.Of(("static_id", shape.StaticID), ("effects", Inspection.EffectInfo(c, shape))));
            }
            c.RecordMutation(observed, observed);
            c.Stage("mutation_observed");
        };
    }

    // Flattens groups to validated leaves: a group is accepted only when EVERY descendant is a supported vector
    // shape or text; bitmap/locked/special layer/PowerClip/non-local effects refuse the whole request before mutation.
    private static VGCore.Shape[] Leaves(OperationContext c, VGCore.Shape[] roots)
    {
        List<VGCore.Shape> leaves = [];
        int visited = 0;
        foreach (var root in roots) Visit(root, 0);
        return leaves.ToArray();
        void Visit(VGCore.Shape shape, int depth)
        {
            if (depth > 32 || ++visited > 50000) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышен предел обхода групп.");
            var layer = c.Track(shape.Layer);
            if (shape.Locked || !layer.Editable) throw new OperationFailure("LOCKED_TARGET", "Фигура или её слой заблокированы: " + shape.StaticID);
            if (layer.IsSpecialLayer) throw new OperationFailure("UNSUPPORTED_SHAPE", "Специальный слой не поддерживается: " + shape.StaticID);
            if (shape.Type == VGCore.cdrShapeType.cdrGroupShape)
            {
                var children = c.Track(shape.Shapes);
                for (int index = 1; index <= children.Count; index++) Visit(c.Track(children[index]), depth + 1);
                return;
            }
            var type = (int)shape.Type;
            if (type is not (1 or 2 or 3 or 4 or 6))
                throw new OperationFailure("UNSUPPORTED_SHAPE", "Тип фигуры не поддерживается прозрачностью: " + shape.StaticID);
            var effects = c.Track(shape.Effects);
            if (effects.Count > 0)
            {
                // CorelDRAW stores ordinary object transparency in the lens collection too. Re-apply/replace through
                // the kind union is allowed only for the single cdrLens effect holding unfrozen uniform/fountain
                // transparency (same predicate as the DrawingTarget effects gate, which this operation bypasses
                // by design: it owns its target validation like PowerClip).
                if (effects.Count != 1 || c.Track(effects[1]).Type != VGCore.cdrEffectType.cdrLens)
                    throw new OperationFailure("UNSUPPORTED_SHAPE", "Эффекты этой фигуры не поддерживаются прозрачностью: " + shape.StaticID);
                var transparency = c.Track(shape.Transparency);
                var lens = c.Track(c.Track(effects[1]).Lens);
                if (transparency.Type is not (VGCore.cdrTransparencyType.cdrUniformTransparency or VGCore.cdrTransparencyType.cdrFountainTransparency)
                    || !DrawingTarget.IsLocalTransparency(transparency.Type, transparency.Frozen, lens.Frozen, lens.UseViewPoint))
                    throw new OperationFailure("UNSUPPORTED_SHAPE", "Поддерживается только незамороженная обычная или градиентная прозрачность: " + shape.StaticID);
            }
            var clip = shape.PowerClip;
            if (clip is not null) { c.Track(clip); throw new OperationFailure("UNSUPPORTED_SHAPE", "Для PowerClip используйте отдельную операцию."); }
            leaves.Add(shape);
        }
    }

    private static void CheckAncestors(OperationContext c, VGCore.Shape shape, HashSet<int> selected)
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
