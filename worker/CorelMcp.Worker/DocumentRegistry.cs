using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker;

internal static class Dto
{
    public static Dictionary<string, object?> Of(params (string key, object? value)[] values)
        => values.ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal);
}

internal sealed class DocumentRegistry(string sessionId, ComOwnership ownership) : IDisposable
{
    internal sealed class Entry(string id, VGCore.Document document, ComScope hold)
    {
        public string Id { get; } = id;
        public VGCore.Document Document { get; } = document;
        public ComScope Hold { get; } = hold;
        public Dictionary<long, (string id, VGCore.Page value)> Pages { get; } = [];
        public Dictionary<(long page, long layer), (string id, string pageId, VGCore.Layer value)> Layers { get; } = [];
    }

    private readonly Dictionary<long, Entry> entries = [];
    public IReadOnlyList<Entry> Sync(VGCore.Application app, ComScope scope)
    {
        HashSet<long> live = [];
        var documents = scope.Track(app.Documents);
        for (int index = 1; index <= documents.Count; index++)
        {
            var doc = scope.Track(documents[index]);
            var identity = ComScope.Identity(doc);
            live.Add(identity);
            if (!entries.ContainsKey(identity))
            {
                var hold = ownership.CreateScope();
                entries.Add(identity, new Entry(sessionId + ":" + Guid.NewGuid().ToString("N"), hold.Track(doc), hold));
            }
        }
        foreach (var identity in entries.Keys.Where(identity => !live.Contains(identity)).ToArray())
        {
            var retired = entries[identity]; entries.Remove(identity); retired.Hold.Dispose();
        }
        return entries.Values.ToArray();
    }

    public Entry Get(VGCore.Application app, ComScope scope, string id)
        => Sync(app, scope).FirstOrDefault(item => item.Id == id)
            ?? throw new OperationFailure("STALE_DOCUMENT", "Документ закрыт или относится к другому сеансу; получите актуальные ID через corel_status.");

    public Entry Register(VGCore.Application app, ComScope scope, VGCore.Document document)
    {
        var identity = ComScope.Identity(document);
        return Sync(app, scope).FirstOrDefault(item => ComScope.Identity(item.Document) == identity)
            ?? throw new OperationFailure("DOCUMENT_NOT_REGISTERED", "Созданный документ не найден по точной COM-идентичности.", null);
    }

    public VGCore.Page Page(Entry entry, ComScope scope, int? index)
    {
        var pages = scope.Track(entry.Document.Pages);
        if (index is not null)
        {
            if (index < 1 || index > pages.Count) throw new OperationFailure("PAGE_NOT_FOUND", "Номер страницы отсутствует в точном документе.");
            return scope.Track(pages[index.Value]);
        }
        return scope.Track(entry.Document.ActivePage);
    }

    public string PageId(Entry entry, VGCore.Page page)
    {
        var identity = PageHandle(page);
        if (!entry.Pages.TryGetValue(identity, out var value))
        {
            value = (sessionId + ":page:" + Guid.NewGuid().ToString("N"), entry.Hold.Track(page));
            entry.Pages.Add(identity, value);
        }
        return value.id;
    }

    public string LayerId(Entry entry, VGCore.Page page, VGCore.Layer layer)
    {
        var identity = (PageHandle(page), LayerHandle(layer));
        if (!entry.Layers.TryGetValue(identity, out var value))
        {
            value = (sessionId + ":layer:" + Guid.NewGuid().ToString("N"), PageId(entry, page), entry.Hold.Track(layer));
            entry.Layers.Add(identity, value);
        }
        return value.id;
    }

    public VGCore.Page ResolvePage(Entry entry, ComScope scope, string pageId)
    {
        var pages = scope.Track(entry.Document.Pages);
        for (int index = 1; index <= pages.Count; index++)
        {
            var page = scope.Track(pages[index]);
            if (entry.Pages.TryGetValue(PageHandle(page), out var value) && value.id == pageId) return page;
        }
        throw new OperationFailure("STALE_PAGE", "Page ID больше не относится к существующей странице этого документа.");
    }

    public VGCore.Layer ResolveLayer(Entry entry, VGCore.Page page, ComScope scope, string layerId)
    {
        var pageId = PageId(entry, page);
        var layers = scope.Track(page.Layers);
        for (int index = 1; index <= layers.Count; index++)
        {
            var layer = scope.Track(layers[index]);
            if (entry.Layers.TryGetValue((PageHandle(page), LayerHandle(layer)), out var value) && value.id == layerId && value.pageId == pageId) return layer;
        }
        throw new OperationFailure("STALE_LAYER", "Layer ID отсутствует на заданной странице точного документа.");
    }

    public Dictionary<string, object?> Info(Entry entry, ComScope scope)
    {
        var doc = entry.Document;
        return Dto.Of(("document_id", entry.Id), ("name", doc.Name ?? ""), ("path", doc.FullFileName ?? ""),
            ("dirty", doc.Dirty), ("pages", scope.Track(doc.Pages).Count), ("active_page", scope.Track(doc.ActivePage).Index),
            ("unit", (int)doc.Unit), ("preserve_selection", doc.PreserveSelection));
    }
    private long PageHandle(VGCore.Page page) { using var scope = ownership.CreateScope(); return scope.Track(page.TreeNode).Handle; }
    private long LayerHandle(VGCore.Layer layer) { using var scope = ownership.CreateScope(); return scope.Track(layer.TreeNode).Handle; }
    public void Dispose()
    {
        List<Exception> failures = [];
        foreach (var entry in entries.Values) { try { entry.Hold.Dispose(); } catch (Exception error) { failures.Add(error); } }
        entries.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
