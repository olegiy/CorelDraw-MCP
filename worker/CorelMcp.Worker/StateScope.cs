using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker;

internal sealed class StateScope : IDisposable
{
    private readonly OperationContext context;
    private readonly VGCore.Document target;
    private readonly VGCore.Document? previous;
    private readonly VGCore.Page page;
    private readonly VGCore.Page? previousPage;
    private readonly VGCore.cdrUnit unit;
    private readonly VGCore.cdrReferencePoint reference;
    private readonly bool preserve, optimization, events, dirty, verifyDirty;
    private readonly int[] selection;
    public StateScope(OperationContext context, bool verifyDirty)
    {
        this.context = context; this.verifyDirty = verifyDirty;
        target = context.Target!.Document;
        previous = context.Track(context.App.Documents).Count > 0 ? context.Track(context.App.ActiveDocument) : null;
        previousPage = previous is null ? null : context.Track(previous.ActivePage);
        page = context.Track(target.ActivePage);
        unit = target.Unit; reference = target.ReferencePoint; preserve = target.PreserveSelection;
        optimization = context.App.Optimization; events = context.App.EventsEnabled;
        dirty = target.Dirty; selection = ShapeTraversal.Selection(context, target);
    }
    public void Dispose()
    {
        var cleanup = context.Cleanup;
        cleanup.Try("state.unit", () => { if (target.Unit != unit) target.Unit = unit; });
        cleanup.Try("state.reference_point", () => { if (target.ReferencePoint != reference) target.ReferencePoint = reference; });
        cleanup.Try("state.page", () => { if (context.Track(context.Track(target.ActivePage).TreeNode).Handle != context.Track(page.TreeNode).Handle) page.Activate(); });
        cleanup.Try("state.selection", RestoreSelection);
        cleanup.Try("state.preserve_selection", () => { if (target.PreserveSelection != preserve) target.PreserveSelection = preserve; });
        cleanup.Try("state.optimization", () => { if (context.App.Optimization != optimization) context.App.Optimization = optimization; });
        cleanup.Try("state.events_enabled", () => { if (context.App.EventsEnabled != events) context.App.EventsEnabled = events; });
        cleanup.Try("state.active_document", () =>
        {
            if (previous is not null && ComScope.Identity(context.Track(context.App.ActiveDocument)) != ComScope.Identity(previous)) previous.Activate();
            if (previousPage is not null && context.Track(context.Track(previous!.ActivePage).TreeNode).Handle != context.Track(previousPage.TreeNode).Handle) previousPage.Activate();
        });
        if (verifyDirty || !context.MutationStarted) cleanup.Try("state.source_dirty", () =>
        {
            if (target.Dirty != dirty) throw new OperationFailure("ORIGINAL_STATE_CHANGED", "Dirty исходного документа изменился во время операции; значение не было принудительно сброшено.", null);
        });
    }
    private void RestoreSelection()
    {
        if (selection.SequenceEqual(ShapeTraversal.Selection(context, target))) return;
        var available = ShapeTraversal.Walk(context, context.Track(page.Shapes)).ToDictionary(item => item.Shape.StaticID, item => item.Shape);
        var range = context.Track(context.App.CreateShapeRange());
        foreach (var id in selection)
        {
            if (available.TryGetValue(id, out var shape)) range.Add(shape);
            else context.Cleanup.MissingSelectionIds.Add(id);
        }
        if (range.Count == 0) target.ClearSelection(); else range.CreateSelection();
    }
}
