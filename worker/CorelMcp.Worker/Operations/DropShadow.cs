namespace CorelMcp.Worker.Operations;

// corel_apply_drop_shadow: typed dispatch over IVGShape.CreateDropShadow and IVGShape.ClearEffect(cdrDropShadow),
// following Operations/PowerClip.cs — resolve + full target validation before the mutation closure.
// Live-probed semantics (2026-11-17, own temp docs, CorelDRAW 27.2.0.135): the target KEEPS its StaticID, type,
// geometry and page/group position; CorelDRAW attaches the cdrDropShadow effect to the target itself and adds a
// NEW sibling cdrDropShadowGroupShape (shadow group + one shadow child). Shape.ClearEffect(cdrDropShadow) removes
// the effect and deletes the whole shadow group, restoring the target with its original StaticID and type. The
// design's wrap premise (old StaticID disappears) is not what API 27 does; id_mappings therefore mirrors the
// convert-to-curves report as source_static_id -> result_static_ids [target, ...shadow subtree] for kind shadow
// and [target] for kind none, with created/deleted coming from RecordMutation.
// v1 target policy (binding): single vector shapes (types 1/2/3/4) and text (6); groups, shadow groups, bitmaps,
// locked or special layers, PowerClip, any existing effect and already-shadowed targets refuse before mutation.
// Shadowed shapes afterwards refuse other mutations through the existing effects gate (documented limitation).
internal static class DropShadow
{
    public static Action Prepare(DrawingTarget target, DropShadowCommand args)
    {
        var c = target.Context;
        var shapes = ShapeTraversal.Resolve(c, target.Page, args.StaticIds);
        var selected = args.StaticIds.ToHashSet();
        List<object> mappings = [];
        c.DrawingDetails["id_mappings"] = mappings;
        if (args.Kind == "none") return PrepareRemoval(target, args, shapes, selected, mappings);
        // Enum mappings and the color are pure and resolved in preflight, like the fountain colors in Styles.
        var type = DrawingContracts.DropShadowType(args.Type!);
        var featherType = DrawingContracts.FeatherType(args.FeatherType!);
        var featherEdge = DrawingContracts.FeatherEdge(args.FeatherEdge!);
        var mergeMode = DrawingContracts.MergeMode(args.MergeMode!);
        var color = Styles.Color(c, args.Color!);
        var targets = shapes.Select(shape => Validate(c, shape, selected, shadow: true)).ToArray();
        return () =>
        {
            foreach (var shape in targets)
            {
                c.Mutating();
                // Offsets are document lengths (unlike fountain center percents): converted with target.U().
                // Perspective fields are not exposed in v1 and pass the live-observed defaults 0/0.
                var effect = c.Track(shape.CreateDropShadow(type, args.Opacity!.Value, args.Feather!.Value,
                    target.U(args.OffsetXMm!.Value), target.U(args.OffsetYMm!.Value), color,
                    featherType, featherEdge, PerspectiveAngle: 0, PerspectiveStretch: 0, args.Fade ?? 0, mergeMode));
                var observed = new[] { shape.StaticID }.Concat(ShadowSubtree(c, c.Track(c.Track(c.Track(effect.DropShadow)).ShadowGroup))).ToArray();
                c.RecordMutation([shape.StaticID], observed);
                mappings.Add(Dto.Of(("source_static_id", shape.StaticID), ("result_static_ids", observed)));
                c.DrawingItems.Add(Observe(target, c, shape));
                c.Stage("mutation_observed");
            }
        };
    }

    private static Action PrepareRemoval(DrawingTarget target, DropShadowCommand args, VGCore.Shape[] shapes, HashSet<int> selected, List<object> mappings)
    {
        var c = target.Context;
        // Removal targets the original shape: it carries the effect (live-probed). Exactly one cdrDropShadow effect
        // is accepted; any other effect state refuses before mutation. merge_mode is ignored here.
        var pending = shapes.Select(shape =>
        {
            Validate(c, shape, selected, shadow: false);
            var effects = c.Track(shape.Effects);
            if (effects.Count == 0)
                throw new OperationFailure("NOT_A_DROP_SHADOW", "У точной фигуры нет тени для снятия: " + shape.StaticID);
            var effect = c.Track(effects[1]);
            if (effects.Count != 1 || effect.Type != VGCore.cdrEffectType.cdrDropShadow)
                throw new OperationFailure("UNSUPPORTED_SHAPE", "Эффекты этой фигуры не поддерживаются снятием тени: " + shape.StaticID);
            return (Shape: shape, Effect: effect);
        }).ToArray();
        return () =>
        {
            foreach (var (shape, effect) in pending)
            {
                c.Mutating();
                // The shadow subtree is captured before removal so deleted ids stay honest.
                var before = new[] { shape.StaticID }.Concat(ShadowSubtree(c, c.Track(c.Track(c.Track(effect.DropShadow)).ShadowGroup))).ToArray();
                shape.ClearEffect(VGCore.cdrEffectType.cdrDropShadow);
                c.RecordMutation(before, [shape.StaticID]);
                mappings.Add(Dto.Of(("source_static_id", shape.StaticID), ("result_static_ids", new[] { shape.StaticID })));
                c.DrawingItems.Add(Observe(target, c, shape));
                c.Stage("mutation_observed");
            }
        };
    }

    // Shared preflight checks: single vector shape (types 1/2/3/4) or text (6); groups, shadow groups (type 16),
    // bitmaps and locked/special targets refuse; kind shadow additionally refuses existing effects and PowerClip.
    private static VGCore.Shape Validate(OperationContext c, VGCore.Shape shape, HashSet<int> selected, bool shadow)
    {
        CheckAncestors(c, shape, selected);
        var layer = c.Track(shape.Layer);
        if (shape.Locked || !layer.Editable) throw new OperationFailure("LOCKED_TARGET", "Фигура или её слой заблокированы: " + shape.StaticID);
        if (layer.IsSpecialLayer) throw new OperationFailure("UNSUPPORTED_SHAPE", "Специальный слой не поддерживается: " + shape.StaticID);
        var type = (int)shape.Type;
        if (type is not (1 or 2 or 3 or 4 or 6))
            throw new OperationFailure("UNSUPPORTED_SHAPE",
                (type == 16 ? "Тень задаётся/снимается на исходной фигуре по её StaticID, а не на группе тени: " : "Тип фигуры не поддерживается тенью: ") + shape.StaticID);
        var clip = shape.PowerClip;
        if (clip is not null) { c.Track(clip); throw new OperationFailure("UNSUPPORTED_SHAPE", "Для PowerClip используйте отдельную операцию."); }
        if (shadow && c.Track(shape.Effects).Count > 0)
            throw new OperationFailure("UNSUPPORTED_SHAPE", "Повторная тень и существующие эффекты не поддерживаются: " + shape.StaticID);
        return shape;
    }

    private static void CheckAncestors(OperationContext c, VGCore.Shape shape, HashSet<int> selected)
    {
        var ancestor = shape.ParentGroup;
        while (ancestor is not null)
        {
            var group = c.Track(ancestor);
            if (group.Locked) throw new OperationFailure("LOCKED_TARGET", "Родительская группа заблокирована.");
            if (selected.Contains(group.StaticID)) throw new OperationFailure("OVERLAPPING_TARGETS", "Группа и её потомок не принимаются вместе.");
            if ((int)group.Type == 16) throw new OperationFailure("UNSUPPORTED_SHAPE", "Фигура внутри группы тени не поддерживается: " + shape.StaticID);
            ancestor = group.ParentGroup;
        }
    }

    // The shadow group is a new sibling of the target (live-probed); its subtree ids feed created/deleted and id_mappings.
    private static IEnumerable<int> ShadowSubtree(OperationContext c, VGCore.Shape group) =>
        new[] { group.StaticID }.Concat(ShapeTraversal.Walk(c, c.Track(group.Shapes)).Select(item => item.Shape.StaticID));

    // Full Info plus the observed effect collection: the report shows the drop shadow (or its absence) as read back.
    private static Dictionary<string, object?> Observe(DrawingTarget target, OperationContext c, VGCore.Shape shape)
    {
        var info = target.Info(shape);
        info["effects"] = Inspection.EffectInfo(c, shape);
        return info;
    }
}
