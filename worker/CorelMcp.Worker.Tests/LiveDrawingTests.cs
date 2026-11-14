using System.Text.Json;
using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveDrawingTests
{
    [LiveCorelFact]
    public async Task NestedGroupDuplicationCountsEveryDescendantAndRejectsOverlappingTargets()
    {
        using var client = new DrawingClient(); var doc = await client.Create(); var layer = NormalLayer(await client.Inspect(doc));
        var created = await client.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 0, y_mm = 0, width_mm = 10, height_mm = 10 },
            new { kind = "rectangle", x_mm = 20, y_mm = 0, width_mm = 10, height_mm = 10 },
            new { kind = "ellipse", x_mm = 40, y_mm = 0, width_mm = 10, height_mm = 10 } } });
        var ids = Ids(created, "created_static_ids");
        var inner = Assert.Single(Ids(await client.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { ids[0], ids[1] } }), "created_static_ids"));
        var outer = Assert.Single(Ids(await client.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { inner, ids[2] } }), "created_static_ids"));
        var before = (await client.Inspect(doc)).GetRawText();
        var excessive = await client.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { outer }, copies = 201, step_x_mm = 1, step_y_mm = 0 });
        Assert.Equal("RESULT_LIMIT_EXCEEDED", excessive.Error!.Code); Assert.False(excessive.Error.Applied);
        var overlap = await client.DrawFailure(doc, "corel_transform_shapes", new { static_ids = new[] { outer, ids[0] }, action = "move", dx_mm = 10, dy_mm = 0 });
        Assert.Equal("OVERLAPPING_TARGETS", overlap.Error!.Code); Assert.False(overlap.Error.Applied);
        Assert.Equal(before, (await client.Inspect(doc)).GetRawText());
        var copied = await client.Draw(doc, "corel_duplicate_shapes", new { static_ids = new[] { outer }, copies = 1, step_x_mm = 0, step_y_mm = 20 });
        Assert.Equal(5, Ids(copied, "created_static_ids").Length);
    }
    [LiveCorelFact]
    public async Task BasicRecipesUsePageCoordinatesAndPreserveControlDocumentAndSelection()
    {
        foreach (var unit in new[] { "mm", "inch" })
        {
            using var client = new DrawingClient();
            var doc = await client.Create();
            await client.Ok("test_configure", new { document_id = doc, unit });
            var binding = await client.Inspect(doc);
            var layer = NormalLayer(binding);
            var control = await client.Create();
            var controlBefore = (await client.Inspect(control)).GetRawText();
            var created = await client.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
                new { kind = "rectangle", x_mm = 10, y_mm = 20, width_mm = 30, height_mm = 10 },
                new { kind = "ellipse", x_mm = 60, y_mm = 20, width_mm = 20, height_mm = 10 },
                new { kind = "line", x1_mm = 10, y1_mm = 50, x2_mm = 40, y2_mm = 60 } } });
            var ids = Ids(created, "created_static_ids"); Assert.Equal(3, ids.Length);
            Bounds(created.GetProperty("items")[0], 10, 20, 30, 10);
            Bounds(created.GetProperty("items")[1], 60, 20, 20, 10);
            await client.Ok("test_select", new { document_id = doc, static_ids = new[] { ids[0] } });
            var moved = await client.Draw(doc, "corel_transform_shapes", new { static_ids = new[] { ids[0] }, action = "move", dx_mm = 5, dy_mm = -2 });
            Bounds(moved.GetProperty("items")[0], 15, 18, 30, 10);
            var scaled = await client.Draw(doc, "corel_transform_shapes", new { static_ids = new[] { ids[0] }, action = "scale", sx = 2, sy = 0.5, anchor_x_mm = 0, anchor_y_mm = 0 });
            Bounds(scaled.GetProperty("items")[0], 30, 9, 60, 5);
            var rotated = await client.Draw(doc, "corel_transform_shapes", new { static_ids = new[] { ids[0] }, action = "rotate", angle_deg = 90, anchor_x_mm = 0, anchor_y_mm = 0 });
            Bounds(rotated.GetProperty("items")[0], -14, 30, 5, 60);
            var flipped = await client.Draw(doc, "corel_transform_shapes", new { static_ids = new[] { ids[0] }, action = "flip", axis = "horizontal", anchor_x_mm = 0, anchor_y_mm = 0 });
            Bounds(flipped.GetProperty("items")[0], 9, 30, 5, 60);
            var copied = await client.Draw(doc, "corel_duplicate_shapes", new { static_ids = new[] { ids[1] }, copies = 2, step_x_mm = 0, step_y_mm = 20 });
            Assert.Equal(2, Ids(copied, "created_static_ids").Length);
            Bounds(copied.GetProperty("items")[0], 60, 40, 20, 10); Bounds(copied.GetProperty("items")[1], 60, 60, 20, 10);
            var styled = await client.Draw(doc, "corel_style_shapes", new { static_ids = new[] { ids[0], ids[1] }, fill = new { kind = "uniform", color = new { model = "rgb", r = 255, g = 0, b = 0 } }, outline = new { kind = "uniform", width_mm = 0.2, color = new { model = "cmyk", c = 0, m = 0, y = 0, k = 100 } } });
            Assert.InRange(styled.GetProperty("items")[0].GetProperty("outline_width_mm").GetDouble(), 0.19999, 0.20001);
            var grouped = await client.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { ids[0], ids[1] } });
            var groupId = Assert.Single(Ids(grouped, "created_static_ids"));
            var excessive = await client.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { groupId }, copies = 334, step_x_mm = 1, step_y_mm = 0 });
            Assert.Equal("RESULT_LIMIT_EXCEEDED", excessive.Error!.Code); Assert.False(excessive.Error.Applied);
            var ungrouped = await client.Draw(doc, "corel_group_shapes", new { action = "ungroup", static_ids = new[] { groupId } });
            Assert.Contains(groupId, Ids(ungrouped, "deleted_static_ids"));
            await client.Draw(doc, "corel_convert_to_curves", new { static_ids = new[] { ids[1] } });
            var text = await client.Draw(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "Ёж — текст", x_mm = 10, y_mm = 90, font_family = "Arial", font_size_pt = 12 });
            Assert.Equal("Ёж — текст", text.GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Equal("Arial", text.GetProperty("items")[0].GetProperty("font_family").GetString());
            await client.Draw(doc, "corel_create_text", new { kind = "paragraph", layer_id = layer, text = "Строка 1\r\nСтрока 2", x_mm = 110, y_mm = 80, width_mm = 60, height_mm = 40, font_family = "Arial", font_size_pt = 11 });
            var after = await client.Inspect(doc);
            Assert.Equal(new[] { ids[0] }, Ids(after, "selection_ids"));
            Assert.Equal(binding.GetProperty("document").GetProperty("unit").GetInt32(), after.GetProperty("document").GetProperty("unit").GetInt32());
            Assert.Equal(binding.GetProperty("reference_point").GetInt32(), after.GetProperty("reference_point").GetInt32());
            Assert.Equal(controlBefore, (await client.Inspect(control)).GetRawText());
            Assert.Equal(control, (await client.Ok("corel_status", new { })).GetProperty("active_document_id").GetString());
        }
    }

    [LiveCorelFact]
    public async Task LayerIdsPreflightAndPartialFailureNeverReplayOrUndo()
    {
        using var client = new DrawingClient(); var doc = await client.Create(); var before = await client.Inspect(doc);
        var layer = NormalLayer(before);
        var pageId = before.GetProperty("page_id").GetString()!;
        var other = await client.Draw(doc, "corel_manage_layers", new { action = "create", page_id = pageId, name = "Одинаково" });
        var otherId = other.GetProperty("layer").GetProperty("layer_id").GetString()!;
        var sameName = await client.Draw(doc, "corel_manage_layers", new { action = "create", page_id = pageId, name = "Одинаково" });
        Assert.NotEqual(otherId, sameName.GetProperty("layer").GetProperty("layer_id").GetString());
        var shapes = await client.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] { new { kind = "rectangle", x_mm = 0, y_mm = 0, width_mm = 10, height_mm = 10 }, new { kind = "rectangle", x_mm = 20, y_mm = 0, width_mm = 10, height_mm = 10 } } });
        var ids = Ids(shapes, "created_static_ids");
        await client.Draw(doc, "corel_manage_layers", new { action = "move_shapes", layer_id = otherId, static_ids = new[] { ids[1] } });
        await client.Draw(doc, "corel_manage_layers", new { action = "set_flags", layer_id = otherId, editable = false });
        var saved = (await client.Inspect(doc)).GetRawText();
        var locked = await client.DrawFailure(doc, "corel_transform_shapes", new { static_ids = ids, action = "move", dx_mm = 10, dy_mm = 0 });
        Assert.Equal("LOCKED_TARGET", locked.Error!.Code); Assert.False(locked.Error.Applied);
        Assert.Equal(saved, (await client.Inspect(doc)).GetRawText());
        await client.Draw(doc, "corel_manage_layers", new { action = "set_flags", layer_id = otherId, editable = true });
        var missingFont = await client.DrawFailure(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "текст", x_mm = 0, y_mm = 20, font_family = "CorelMcp_NoSuchFont_71823", font_size_pt = 12 });
        Assert.Equal("FONT_NOT_INSTALLED", missingFont.Error!.Code); Assert.False(missingFont.Error.Applied);
        client.FailAfterMutation = true;
        var partial = await client.DrawFailure(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] { new { kind = "rectangle", x_mm = 40, y_mm = 0, width_mm = 10, height_mm = 10 }, new { kind = "rectangle", x_mm = 60, y_mm = 0, width_mm = 10, height_mm = 10 } } });
        Assert.True(partial.Error!.Applied); Assert.False(partial.Ok);
        var details = JsonSerializer.SerializeToElement(partial.Result, Protocol.JsonOptions);
        Assert.True(details.GetProperty("partial").GetBoolean()); Assert.Single(Ids(details, "created_static_ids"));
        Assert.Equal(3, (await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
        Assert.True((await client.Inspect(doc)).GetProperty("document").GetProperty("dirty").GetBoolean());
    }

    [LiveCorelFact]
    public async Task TrimDirectionAndKeepFlagsReturnObservedIds()
    {
        foreach (var action in new[] { "trim", "weld", "intersect" })
        foreach (var keep in new[] { false, true })
        {
            using var client = new DrawingClient(); var doc = await client.Create(); var layer = NormalLayer(await client.Inspect(doc));
            var shapes = await client.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] { new { kind = "rectangle", x_mm = 0, y_mm = 0, width_mm = 10, height_mm = 10 }, new { kind = "rectangle", x_mm = 5, y_mm = 0, width_mm = 10, height_mm = 10 } } });
            var ids = Ids(shapes, "created_static_ids");
            var result = await client.Draw(doc, "corel_boolean_shapes", new { action, left_id = ids[0], right_id = ids[1], keep_left = keep, keep_right = keep });
            var item = result.GetProperty("items")[0];
            Bounds(item, action == "trim" ? 10 : action == "intersect" ? 5 : 0, 0, action == "weld" ? 15 : 5, 10);
            var finalIds = (await client.Inspect(doc)).GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).ToArray();
            Assert.Equal(keep, finalIds.Contains(ids[0])); Assert.Equal(keep, finalIds.Contains(ids[1]));
            foreach (var id in Ids(result, "created_static_ids")) Assert.Contains(id, finalIds);
            foreach (var id in Ids(result, "deleted_static_ids")) Assert.DoesNotContain(id, finalIds);
        }
    }
    private static string NormalLayer(JsonElement inspection) => inspection.GetProperty("layers").EnumerateArray().First(layer => !layer.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    private static int[] Ids(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(id => id.GetInt32()).ToArray();
    private static void Bounds(JsonElement item, double x, double y, double w, double h)
    {
        var b = item.GetProperty("bounds_mm");
        foreach (var (key, expected) in new[] { ("left", x), ("bottom", y), ("width", w), ("height", h) })
            Assert.InRange(b.GetProperty(key).GetDouble(), expected - 0.0001, expected + 0.0001);
    }

    internal sealed class DrawingAdapter : IOperationDispatcher
    {
        private readonly WorkerSession session; private readonly OperationDispatcher native;
        public HashSet<string> Owned { get; } = []; public int FailAfterMutations; public bool FailBeforeDrawing;
        public DrawingAdapter(CorelMcp.Worker.Hybrid.ExecutionMode? mode=null,Func<CorelMcp.Worker.Hybrid.INativeVbaRuntime>? nativeFactory=null) { session=new(mode,nativeFactory);native = new(session); }
        public WorkerResult Execute(WorkerRequest request, Action<string> progress)
        {
            if (request.Tool.StartsWith("test_", StringComparison.Ordinal))
            {
                var id = request.Args.GetProperty("document_id").GetString()!; Assert.Contains(id, Owned);
                using var lease = ComOperationMutex.Acquire(); var cleanup = new CleanupRecorder();
                using var context = new OperationContext(session, request, progress, cleanup);
                var doc = context.Registry.Get(context.App, context.Scope, id).Document;
                if(request.Tool=="test_native_geometry_error")
                {
                    var runtime=session.Native;runtime.EnsureReady(context);
                    var empty=context.Track(context.App.CreateShapeRange());
                    var error=Assert.Throws<OperationFailure>(()=>runtime.ExtractGeometry(context,doc,empty,[],1,Guid.NewGuid().ToString("N")));
                    return new(true,Dto.Of(("error_code",error.Code)),Cleanup:cleanup.Snapshot());
                }
                if(request.Tool=="test_close_after_unknown")
                {
                    // Test-only teardown after readback established a finished native call on an owned fixture.
                    doc.Dirty=false;doc.Close();Owned.Remove(id);
                    return new(true,Dto.Of(("closed_owned_fixture",true)),Cleanup:cleanup.Snapshot());
                }
                if (request.Tool == "test_configure")
                {
                    doc.Unit = VGCore.cdrUnit.cdrMillimeter; doc.DrawingOriginX = 17; doc.DrawingOriginY = -13;
                    doc.ReferencePoint = VGCore.cdrReferencePoint.cdrTopRight;
                    if (request.Args.GetProperty("unit").GetString() == "inch") doc.Unit = VGCore.cdrUnit.cdrInch;
                }
                else if (request.Tool == "test_select")
                {
                    var ids = request.Args.GetProperty("static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                    var range = context.Track(context.App.CreateShapeRange());
                    foreach (var shape in ShapeTraversal.Resolve(context, context.Track(doc.ActivePage), ids)) range.Add(shape);
                    range.CreateSelection();
                }
                else if(request.Tool=="test_lock_shape")
                {
                    var shape=ShapeTraversal.Resolve(context,context.Track(doc.ActivePage),[request.Args.GetProperty("static_id").GetInt32()])[0];
                    shape.Locked=request.Args.GetProperty("locked").GetBoolean();
                }
                else if(request.Tool is "test_pattern_fill" or "test_texture_probe")
                {
                    var ids=request.Args.GetProperty("static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
                    var shapes=ShapeTraversal.Resolve(context,context.Track(doc.ActivePage),ids);
                    if(request.Tool=="test_pattern_fill")
                    {
                        string path=Path.GetFullPath(request.Args.GetProperty("path").GetString()!);
                        Assert.StartsWith(Path.GetFullPath(request.RunDirectory)+Path.DirectorySeparatorChar,path,StringComparison.OrdinalIgnoreCase);
                        foreach(var shape in shapes)context.Track(context.Track(shape.Fill).ApplyPatternFill(VGCore.cdrPatternFillType.cdrFullColorPattern,path));
                    }
                    else
                    {
                        try
                        {
                            var texture=context.Track(context.Track(shapes[0].Fill).ApplyTextureFill("Blend Edges","Styles"));
                            string name=texture.TextureName,library=texture.LibraryName,style=texture.StyleName;
                            bool confirmed=string.Equals(name,"Blend Edges",StringComparison.OrdinalIgnoreCase)&&string.Equals(library,"Styles",StringComparison.OrdinalIgnoreCase);
                            return new(true,Dto.Of(("status",confirmed?"preset_confirmed":"preset_unconfirmed"),("texture_name",name),("library_name",library),("style_name",style)),Cleanup:cleanup.Snapshot());
                        }
                        catch(System.Runtime.InteropServices.COMException error)
                        {return new(true,Dto.Of(("status","unsupported"),("hresult",error.HResult)),Cleanup:cleanup.Snapshot());}
                    }
                }
                else if (request.Tool is "test_text_mixed" or "test_text_character")
                {
                    var inspectedPage=request.Args.TryGetProperty("page_index",out var pageIndex)
                        ? context.Track(context.Track(doc.Pages)[pageIndex.GetInt32()]) : context.Track(doc.ActivePage);
                    var shape = ShapeTraversal.Resolve(context, inspectedPage, [request.Args.GetProperty("static_id").GetInt32()])[0];
                    var story = context.Track(context.Track(shape.Text).Story);
                    var chars = context.Track(story.Characters);
                    if (request.Tool == "test_text_mixed")
                    {
                        var first = context.Track(chars[1, 2]); var middle = context.Track(chars[7, 2]);
                        Assert.Equal("АА", first.WideText); Assert.Equal("ББ", middle.WideText);
                        first.Bold = true; middle.Italic = true;
                    }
                    else
                    {
                        var range = context.Track(chars[request.Args.GetProperty("index").GetInt32(), 1]);
                        return new(true, Dto.Of(("text", range.WideText), ("bold", range.Bold), ("italic", range.Italic), ("font", range.Font), ("size", range.Size),
                            ("alignment", (int)story.Alignment), ("line_spacing", story.LineSpacing), ("line_spacing_type", (int)story.LineSpacingType)), Cleanup: cleanup.Snapshot());
                    }
                }
                return new(true, Dto.Of(("done", true)), Cleanup: cleanup.Snapshot());
            }
            var outcome = native.Execute(request, stage =>
            {
                progress(stage);
                if (stage == "drawing" && FailBeforeDrawing) { FailBeforeDrawing = false; throw new IOException("Тестовый отказ до начала изменения документа."); }
                if (stage == "mutation_observed" && FailAfterMutations > 0 && --FailAfterMutations == 0) throw new IOException("Тестовый сбой после фактической мутации.");
            });
            if (outcome.Ok && request.Tool == "corel_create_document") Owned.Add((string)((Dictionary<string, object?>)outcome.Result!)["document_id"]!);
            if(outcome.Ok&&request.Tool=="corel_open_document"&&Path.GetFullPath(request.Args.GetProperty("path").GetString()!).StartsWith(Path.GetFullPath(request.RunDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                Owned.Add((string)((Dictionary<string,object?>)outcome.Result!)["document_id"]!);
            if (outcome.Ok && request.Tool == "corel_close_document") Owned.Remove(request.Args.GetProperty("document_id").GetString()!);
            return outcome;
        }
        public void Dispose() => native.Dispose();
    }
    internal sealed class DrawingClient : IDisposable
    {
        private readonly StaHost host; private DrawingAdapter? adapter;
        public bool FailAfterMutation { set => adapter!.FailAfterMutations = value ? 1 : 0; }
        public int FailAfterMutations { set => adapter!.FailAfterMutations = value; }
        public bool FailBeforeDrawing { set => adapter!.FailBeforeDrawing = value; }
        public string Directory { get; } = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), "artifacts", "worker-drawing", Guid.NewGuid().ToString("N"));
        public DrawingClient(CorelMcp.Worker.Hybrid.ExecutionMode? mode=null,Func<CorelMcp.Worker.Hybrid.INativeVbaRuntime>? nativeFactory=null) { System.IO.Directory.CreateDirectory(Directory); host = new StaHost(() => adapter = new(mode,nativeFactory)); host.Ready.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        public async Task<string> Create() => (await Ok("corel_create_document", new { request_id = Guid.NewGuid().ToString(), width_mm = 200, height_mm = 150 })).GetProperty("document_id").GetString()!;
        public Task<JsonElement> Inspect(string doc) => Ok("corel_inspect_document", new { document_id = doc, recursive = true, include_text = true });
        private static Dictionary<string, object?> Bind(string doc, object args)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
            fields["request_id"] = Guid.NewGuid().ToString(); fields["document_id"] = doc; fields["page_index"] = 1; return fields;
        }
        public Task<JsonElement> Draw(string doc, string tool, object args) => Ok(tool, Bind(doc, args));
        public async Task<WorkerResult> DrawFailure(string doc, string tool, object args) { var result = await Call(tool, Bind(doc, args)); Assert.False(result.Ok); return result; }
        public async Task<JsonElement> Ok(string tool, object args)
        {
            var result = await Call(tool, args); Assert.True(result.Ok, JsonSerializer.Serialize(result, Protocol.JsonOptions) + "\n" + Directory);
            Assert.Equal(true, result.Cleanup!["completed"]); return JsonSerializer.SerializeToElement(result.Result, Protocol.JsonOptions);
        }
        internal async Task<WorkerResult> Call(string tool, object args)
        {
            var completion = new TaskCompletionSource<WorkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(host.TrySubmit(new(Guid.NewGuid().ToString(), tool, JsonSerializer.SerializeToElement(args), Directory), _ => { }, value => completion.SetResult(value)));
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); while (host.ActiveJobId is not null) await Task.Delay(1);
            File.AppendAllText(Path.Combine(Directory, "results.jsonl"), JsonSerializer.Serialize(new { tool, args, result }, Protocol.JsonOptions) + "\n"); return result;
        }
        public void Dispose()
        {
            List<string> failures = [];
            foreach (var doc in adapter!.Owned.ToArray())
                try { var result = Call("corel_close_document", new { request_id = Guid.NewGuid().ToString(), document_id = doc, discard_changes = true }).GetAwaiter().GetResult(); if (!result.Ok) failures.Add(JsonSerializer.Serialize(result, Protocol.JsonOptions)); } catch (Exception error) { failures.Add(error.Message); }
            File.WriteAllText(Path.Combine(Directory, "cleanup.json"), JsonSerializer.Serialize(new { completed = failures.Count == 0, failures }));
            host.CompleteInput(); host.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); Assert.Empty(failures);
        }
    }
}
