namespace CorelMcp.Worker.Operations;

internal static class Layers
{
    public static Action Prepare(DrawingTarget target, LayerCommand args)
    {
        var c = target.Context;
        switch (args)
        {
            case CreateLayerCommand create:
                var page = c.Registry.ResolvePage(c.Target!, c.Scope, create.PageId);
                if (c.Track(page.TreeNode).Handle != c.Track(target.Page.TreeNode).Handle) throw new OperationFailure("PAGE_MISMATCH", "page_id и page_index относятся к разным страницам.");
                return () => { c.Mutating(); var layer = c.Track(page.CreateLayer(create.Name)); Observed(target, layer); };
            case RenameLayerCommand rename:
                var renamed = target.Layer(rename.LayerId);
                return () => { c.Mutating(); renamed.Name = rename.Name; Observed(target, renamed); };
            case LayerFlagsCommand flags:
                var flagged = target.Layer(flags.LayerId, false);
                return () =>
                {
                    if (flags.Visible is not null) { c.Mutating(); flagged.Visible = flags.Visible.Value; Observed(target, flagged); }
                    if (flags.Printable is not null) { c.Mutating(); flagged.Printable = flags.Printable.Value; Observed(target, flagged); }
                    if (flags.Editable is not null) { c.Mutating(); flagged.Editable = flags.Editable.Value; Observed(target, flagged); }
                };
            case MoveToLayerCommand move:
                var destination = target.Layer(move.LayerId); var shapes = target.Resolve(move.StaticIds, bitmap: true);
                if (shapes.Any(shape => shape.ParentGroup is not null)) throw new OperationFailure("UNSUPPORTED_SHAPE", "Перенос части группы требует явного разгруппирования.");
                return () => { foreach (var shape in shapes) { var ids = target.TreeIds(shape); c.Mutating(); shape.MoveToLayer(destination); target.Observe(ids, shape); } };
            default: throw new OperationFailure("INVALID_INPUT", "Неподдерживаемая команда слоёв.");
        }
    }
    private static void Observed(DrawingTarget target, VGCore.Layer layer)
    {
        var c = target.Context;
        c.RecordMutation([], []);
        c.DrawingDetails["layer"] = Dto.Of(("layer_id", c.Registry.LayerId(c.Target!, target.Page, layer)), ("name", layer.Name),
            ("visible", layer.Visible), ("editable", layer.Editable), ("printable", layer.Printable));
        c.Stage("mutation_observed");
    }
}
