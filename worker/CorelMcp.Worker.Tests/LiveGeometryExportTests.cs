using System.Text.Json;
using System.Globalization;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveGeometryExportTests
{
    [LiveCorelFact]
    public async Task GeometryClonesKeepPhysicalCoordinatesForMmInchGroupsAndTheirChildren()
    {
        foreach(var mode in new[]{Hybrid.ExecutionMode.CSharp,Hybrid.ExecutionMode.Hybrid})
        foreach (var unit in new[] { "mm", "inch" })
        {
            using var culture = new CultureScope(unit == "mm" ? "ru-RU" : "en-US");
            using var client = new FixtureClient(mode);
            var source = await client.Create();
            var fixture = await client.Ok("corel_test_fixture", new { document_id = source, unit });
            await client.Ok("corel_save_document", new { request_id = "save-fixture", document_id = source, path = Path.Combine(client.Directory, "fixture.cdr") });
            var control = await client.Create();
            var before = await client.Ok("corel_inspect_document", new { document_id = source, recursive = true, include_text = true });
            int rect = fixture.GetProperty("rectangle_id").GetInt32(), ellipse = fixture.GetProperty("ellipse_id").GetInt32();
            int group = fixture.GetProperty("group_id").GetInt32(), child = fixture.GetProperty("group_child_id").GetInt32();
            var snapshot = await client.Ok("corel_measure_shapes", new { document_id = source, static_ids = new[] { rect, ellipse, group, child, fixture.GetProperty("text_id").GetInt32(), fixture.GetProperty("outline_id").GetInt32() } });
            Assert.True(snapshot.GetProperty("original_state_preserved").GetBoolean());
            if(mode==Hybrid.ExecutionMode.Hybrid)Assert.Equal("vba",snapshot.GetProperty("execution_engine").GetString());
            using var json = JsonDocument.Parse(File.ReadAllText(snapshot.GetProperty("snapshot_path").GetString()!));
            var objects = json.RootElement.GetProperty("objects");
            Assert.Equal(6, objects.GetArrayLength());
            var segments = objects[0].GetProperty("segments").EnumerateArray().ToArray();
            var xs = segments.SelectMany(item => new[] { item.GetProperty("p0").GetProperty("x").GetDouble(), item.GetProperty("p1").GetProperty("x").GetDouble() }).ToArray();
            var ys = segments.SelectMany(item => new[] { item.GetProperty("p0").GetProperty("y").GetDouble(), item.GetProperty("p1").GetProperty("y").GetDouble() }).ToArray();
            Assert.InRange(xs.Max() - xs.Min(), 29.999999, 30.000001);
            Assert.InRange(ys.Max() - ys.Min(), 19.999999, 20.000001);
            Assert.Contains(objects[1].GetProperty("segments").EnumerateArray(), item => item.GetProperty("kind").GetString() == "cubic");
            Assert.NotEmpty(objects[4].GetProperty("segments").EnumerateArray());
            var outlinePoints = objects[5].GetProperty("segments").EnumerateArray().SelectMany(segment => segment.EnumerateObject().Where(property => property.Name.StartsWith('p')).Select(property => property.Value)).ToArray();
            Assert.InRange(outlinePoints.Max(point => point.GetProperty("x").GetDouble()) - outlinePoints.Min(point => point.GetProperty("x").GetDouble()), 10.199999, 10.200001);
            Assert.Equal(before.GetRawText(), (await client.Ok("corel_inspect_document", new { document_id = source, recursive = true, include_text = true })).GetRawText());
            Assert.Equal(control, (await client.Ok("corel_status", new { })).GetProperty("active_document_id").GetString());
            var unsupported = await client.Call("corel_measure_shapes", new { document_id = source, static_ids = new[] { fixture.GetProperty("no_paint_id").GetInt32() } });
            Assert.Equal("UNSUPPORTED_GEOMETRY", unsupported.Error!.Code);
            Assert.False(unsupported.Error.Applied);
        }
    }

    [LiveCorelFact]
    public async Task FailedExportPublicationKeepsTheDestinationAndRestoresTheSource()
    {
        using var client = new FixtureClient();
        var doc = await client.Create();
        await client.Ok("corel_test_fixture", new { document_id = doc, unit = "mm" });
        await client.Ok("corel_save_document", new { request_id = "save-fault-fixture", document_id = doc, path = Path.Combine(client.Directory, "fixture.cdr") });
        var before = await client.Ok("corel_inspect_document", new { document_id = doc, recursive = true, include_text = true });
        var path = Path.Combine(client.Directory, "locked.png");
        File.WriteAllText(path, "sentinel");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await client.Call("corel_export", new { request_id = "locked-output", document_id = doc, path, format = "png" });
            Assert.False(failed.Ok);
            Assert.Equal(true, failed.Cleanup!["completed"]);
            Assert.Empty(failed.Artifacts!);
            using var read = new StreamReader(locked, leaveOpen: true);
            Assert.Equal("sentinel", read.ReadToEnd());
        }
        Assert.Empty(System.IO.Directory.GetFiles(client.Directory, ".corelmcp-*"));
        Assert.Equal(before.GetRawText(), (await client.Ok("corel_inspect_document", new { document_id = doc, recursive = true, include_text = true })).GetRawText());
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        public CultureScope(string name) { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); }
        public void Dispose() { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }

    [LiveCorelFact]
    public async Task PageAndLayerIdsSurviveWrapperChangesAndReorderingButNeverBindADeletedPage()
    {
        using var client = new FixtureClient();
        var doc = await client.Create();
        await client.Ok("corel_test_identity_edit", new { document_id = doc, action = "add" });
        var first = await client.Ok("corel_inspect_document", new { document_id = doc, page_index = 1 });
        var second = await client.Ok("corel_inspect_document", new { document_id = doc, page_index = 2 });
        var firstId = first.GetProperty("page_id").GetString()!;
        Assert.NotEqual(firstId, second.GetProperty("page_id").GetString());
        var duplicates = first.GetProperty("layers").EnumerateArray().Where(layer => layer.GetProperty("name").GetString() == "Одинаково").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.NotEqual(duplicates[0].GetProperty("layer_id").GetString(), duplicates[1].GetProperty("layer_id").GetString());
        await client.Ok("corel_test_identity_edit", new { document_id = doc, action = "move", page_id = firstId });
        var moved = await client.Ok("corel_inspect_document", new { document_id = doc, page_index = 2 });
        Assert.Equal(firstId, moved.GetProperty("page_id").GetString());
        await client.Ok("corel_test_identity_edit", new { document_id = doc, action = "delete", page_id = firstId });
        var stale = await client.Call("corel_test_identity_edit", new { document_id = doc, action = "resolve", page_id = firstId });
        Assert.Equal("STALE_PAGE", stale.Error!.Code);
    }

    [LiveCorelFact]
    public async Task ExportsAllExistingFormatsAndPreviewWithoutChangingTheSource()
    {
        using var client = new FixtureClient();
        var source = await client.Create();
        await client.Ok("corel_test_fixture", new { document_id = source, unit = "mm" });
        await client.Ok("corel_save_document", new { request_id = "save-fixture", document_id = source, path = Path.Combine(client.Directory, "fixture.cdr") });
        var control = await client.Create();
        var before = await client.Ok("corel_inspect_document", new { document_id = source, recursive = true, include_text = true });
        foreach (var format in new[] { "png", "svg", "pdf", "jpg", "tiff", "ai", "eps", "dxf", "plt" })
        {
            var path = Path.Combine(client.Directory, "Экспорт " + format + "." + format);
            File.WriteAllText(path, "sentinel");
            var output = await client.Ok("corel_export", new { request_id = "export-" + format, document_id = source, format, path });
            Assert.True(output.GetProperty("bytes").GetInt64() > 8);
            Assert.NotEqual("sentinel", File.ReadAllText(path));
            Assert.Equal(control,(await client.Ok("corel_status",new {})).GetProperty("active_document_id").GetString());
        }
        var preview = await client.Ok("corel_preview", new { document_id = source, max_size = 600 });
        var bytes = File.ReadAllBytes(preview.GetProperty("path").GetString()!);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        Assert.Equal(600, Math.Max(width, height));
        Assert.Equal(before.GetRawText(), (await client.Ok("corel_inspect_document", new { document_id = source, recursive = true, include_text = true })).GetRawText());
        Assert.Equal(control, (await client.Ok("corel_status", new { })).GetProperty("active_document_id").GetString());
    }

    // This compiled fixture adapter lives only in the test assembly. No model code enters the production worker.
    private sealed class FixtureDispatcher(WorkerSession session) : IOperationDispatcher
    {
        private readonly OperationDispatcher native = new(session);
        public HashSet<string> Owned { get; } = [];
        public WorkerResult Execute(WorkerRequest request, Action<string> progress)
        {
            if (request.Tool == "corel_test_identity_edit") return IdentityFixture(request);
            if (request.Tool != "corel_test_fixture")
            {
                var outcome = native.Execute(request, progress);
                if (request.Tool == "corel_create_document" && outcome.Ok) Owned.Add((string)((Dictionary<string, object?>)outcome.Result!)["document_id"]!);
                if (request.Tool == "corel_close_document" && outcome.Ok) Owned.Remove(request.Args.GetProperty("document_id").GetString()!);
                return outcome;
            }
            var id = request.Args.GetProperty("document_id").GetString()!;
            Assert.Contains(id, Owned);
            using var lease = ComOperationMutex.Acquire();
            var cleanup = new CleanupRecorder();
            using var context = new OperationContext(session, request, progress, cleanup);
            _ = context.App;
            var doc = context.Registry.Get(context.App, context.Scope, id).Document;
            doc.Activate(); doc.Unit = VGCore.cdrUnit.cdrMillimeter;
            var layer = context.Track(doc.ActiveLayer);
            var color = context.Track(context.App.CreateRGBColor(40, 120, 210));
            VGCore.Shape Rectangle(double x, double y, double w, double h)
            {
                var shape = context.Track(layer.CreateRectangle2(x, y, w, h));
                context.Track(shape.Fill).ApplyUniformFill(color);
                context.Track(shape.Outline).SetNoOutline();
                return shape;
            }
            var rect = Rectangle(10, 10, 30, 20);
            var ellipse = context.Track(layer.CreateEllipse2(65, 25, 10, 5));
            context.Track(ellipse.Fill).ApplyUniformFill(color); context.Track(ellipse.Outline).SetNoOutline();
            var text = context.Track(layer.CreateArtisticTextWide(10, 60, "Ёж — проверка", Font: "Arial"));
            context.Track(text.Outline).SetNoOutline();
            var child = Rectangle(10, 90, 20, 10);
            var other = Rectangle(40, 90, 10, 10);
            var range = context.Track(context.App.CreateShapeRange()); range.Add(child); range.Add(other);
            var group = context.Track(range.Group());
            var noPaint = Rectangle(80, 90, 5, 5); context.Track(noPaint.Fill).ApplyNoFill();
            var outline = Rectangle(110, 10, 10, 10); context.Track(outline.Fill).ApplyNoFill(); context.Track(outline.Outline).SetProperties(Width: 0.2);
            rect.CreateSelection();
            if (request.Args.GetProperty("unit").GetString() == "inch") doc.Unit = VGCore.cdrUnit.cdrInch;
            return new WorkerResult(true, Dto.Of(("rectangle_id", rect.StaticID), ("ellipse_id", ellipse.StaticID), ("text_id", text.StaticID),
                ("group_id", group.StaticID), ("group_child_id", child.StaticID), ("no_paint_id", noPaint.StaticID), ("outline_id", outline.StaticID)), Cleanup: cleanup.Snapshot());
        }
        private WorkerResult IdentityFixture(WorkerRequest request)
        {
            var id = request.Args.GetProperty("document_id").GetString()!; Assert.Contains(id, Owned);
            using var lease = ComOperationMutex.Acquire(); var cleanup = new CleanupRecorder();
            using var context = new OperationContext(session, request, _ => { }, cleanup);
            _ = context.App; var entry = context.Registry.Get(context.App, context.Scope, id);
            try
            {
                var action = request.Args.GetProperty("action").GetString();
                if (action == "add")
                {
                    context.Track(entry.Document.AddPages(1));
                    var page = context.Registry.Page(entry, context.Scope, 1);
                    context.Track(page.CreateLayer("Одинаково")); context.Track(page.CreateLayer("Одинаково"));
                }
                else
                {
                    var page = context.Registry.ResolvePage(entry, context.Scope, request.Args.GetProperty("page_id").GetString()!);
                    if (action == "move") page.MoveTo(2);
                    if (action == "delete") { page.Delete(); context.Track(entry.Document.AddPages(1)); }
                }
                return new WorkerResult(true, Dto.Of(("done", true)), Cleanup: cleanup.Snapshot());
            }
            catch (Operations.OperationFailure error) { return new WorkerResult(false, Error: new WorkerError(error.Code, error.Message, "fixture", false), Cleanup: cleanup.Snapshot()); }
        }
        public void Dispose() => native.Dispose();
    }

    private sealed class FixtureClient : IDisposable
    {
        private readonly StaHost host;
        private FixtureDispatcher? fixture;
        public string Directory { get; } = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), "artifacts", "worker-geometry-export", Guid.NewGuid().ToString("N"));
        private readonly Hybrid.ExecutionMode? mode;
        public FixtureClient(Hybrid.ExecutionMode? mode=null)
        {
            this.mode=mode;
            System.IO.Directory.CreateDirectory(Directory);
            host = new StaHost(() => fixture = new FixtureDispatcher(new WorkerSession(mode)));
            host.Ready.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        public async Task<string> Create() => (await Ok("corel_create_document", new { request_id = Guid.NewGuid().ToString(), width_mm = 200, height_mm = 150 })).GetProperty("document_id").GetString()!;
        public async Task<JsonElement> Ok(string tool, object args)
        {
            var value = await Call(tool, args);
            Assert.True(value.Ok, JsonSerializer.Serialize(value, Protocol.JsonOptions) + "\n" + Directory);
            Assert.Equal(true, value.Cleanup!["completed"]);
            return JsonSerializer.SerializeToElement(value.Result, Protocol.JsonOptions);
        }
        public async Task<WorkerResult> Call(string tool, object args)
        {
            var request = new WorkerRequest(Guid.NewGuid().ToString(), tool, JsonSerializer.SerializeToElement(args), Directory);
            var completion = new TaskCompletionSource<WorkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(host.TrySubmit(request, _ => { }, result => completion.SetResult(result)));
            var value = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
            while (host.ActiveJobId is not null) await Task.Delay(1);
            File.AppendAllText(Path.Combine(Directory, "results.jsonl"), JsonSerializer.Serialize(new { tool, args, value }, Protocol.JsonOptions) + "\n");
            return value;
        }
        public void Dispose()
        {
            List<string> failures = [];
            foreach (var id in fixture!.Owned.ToArray())
            {
                try { var result = Call("corel_close_document", new { request_id = Guid.NewGuid().ToString(), document_id = id, discard_changes = true }).GetAwaiter().GetResult(); if (!result.Ok) failures.Add(JsonSerializer.Serialize(result, Protocol.JsonOptions)); }
                catch (Exception error) { failures.Add(error.ToString()); }
            }
            File.WriteAllText(Path.Combine(Directory, "cleanup.json"), JsonSerializer.Serialize(new { completed = failures.Count == 0, failures }));
            host.CompleteInput();
            host.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.Empty(failures);
            if(mode==Hybrid.ExecutionMode.Hybrid)LiveNativeBatchTests.UnloadTestRuntime();
        }
    }
}
