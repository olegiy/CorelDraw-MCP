using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using Xunit;
using DrawingClient = CorelMcp.Worker.Tests.LiveDrawingTests.DrawingClient;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveNativeDuplicationTests
{
    [LiveCorelFact]
    public async Task GroupedCardGridUsesVbaAndReportsEveryCopyDescendant()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid);
        var baseline = await c.Ok("corel_status", new { });
        var doc = (await c.Ok("corel_create_document", new { request_id = Guid.NewGuid().ToString(), width_mm = 1020, height_mm = 400 })).GetProperty("document_id").GetString()!;
        var (group, _, _) = await CreateCard(c, doc);
        var original = await c.Inspect(doc);
        var control = await c.Create(); var controlBefore = await c.Inspect(control);
        var grid = await c.Draw(doc, "corel_grid_duplicate", new { static_ids = new[] { group }, rows = 8, columns = 12, anchor_x_mm = 0, anchor_y_mm = 0 });
        Assert.True(grid.TryGetProperty("execution_engine", out var gridEngine), "Large grouped grid did not use the native route.");
        Assert.Equal("vba", gridEngine.GetString());
        Assert.Equal(380, grid.GetProperty("created_static_ids").GetArrayLength());
        Assert.Equal(96, grid.GetProperty("actual_bounds").GetArrayLength());
        var after = await c.Ok("corel_inspect_document", new { document_id = doc, recursive = true, include_text = true, limit = 500 });
        Assert.Equal(JsonValueKind.Null, after.GetProperty("next_offset").ValueKind);
        Assert.Equal(96, after.GetProperty("total_shapes").GetInt32());
        var shapes = after.GetProperty("shapes").EnumerateArray().ToArray();
        Assert.Equal(384, shapes.Length);
        var roots = shapes.Where(s => s.GetProperty("parent_static_id").ValueKind == JsonValueKind.Null).ToArray();
        Assert.Equal(96, roots.Length);
        for (int cell = 0; cell < 96; cell++)
        {
            var placement = grid.GetProperty("actual_bounds")[cell];
            double x = cell % 12 * 85, y = cell / 12 * 50;
            Assert.Equal(x, placement.GetProperty("x_mm").GetDouble(), 3);
            Assert.Equal(y, placement.GetProperty("y_mm").GetDouble(), 3);
            Assert.Equal(85, placement.GetProperty("width_mm").GetDouble(), 3);
            Assert.Equal(50, placement.GetProperty("height_mm").GetDouble(), 3);
            int rootId = placement.GetProperty("static_id").GetInt32();
            Assert.Contains(roots, root => root.GetProperty("static_id").GetInt32() == rootId);
            var children = shapes.Where(s => s.GetProperty("parent_static_id").ValueKind == JsonValueKind.Number && s.GetProperty("parent_static_id").GetInt32() == rootId).ToArray();
            Assert.Equal(3, children.Length);
            foreach (var source in original.GetProperty("shapes").EnumerateArray().Where(s => s.GetProperty("type").GetInt32() != 7))
            {
                var copy = Assert.Single(children, s => s.GetProperty("type").GetInt32() == source.GetProperty("type").GetInt32()
                    && Text(s) == Text(source));
                foreach (var key in new[] { "left", "bottom", "width", "height" })
                {
                    double delta = key == "left" ? x : key == "bottom" ? y : 0;
                    Assert.Equal(source.GetProperty("bounds_mm").GetProperty(key).GetDouble() + delta, copy.GetProperty("bounds_mm").GetProperty(key).GetDouble(), 3);
                }
                Assert.Equal(source.GetProperty("appearance").GetRawText(), copy.GetProperty("appearance").GetRawText());
            }
        }
        var originalIds = original.GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).ToHashSet();
        Assert.Equal(shapes.Select(s => s.GetProperty("static_id").GetInt32()).Where(id => !originalIds.Contains(id)).Order(),
            grid.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).Order());
        foreach (var source in original.GetProperty("shapes").EnumerateArray())
            Assert.Equal(source.GetRawText(), Assert.Single(shapes, s => s.GetProperty("static_id").GetInt32() == source.GetProperty("static_id").GetInt32()).GetRawText());
        Assert.Equal(controlBefore.GetRawText(), (await c.Inspect(control)).GetRawText());
        var status = await c.Ok("corel_status", new { });
        Assert.Equal(control, status.GetProperty("active_document_id").GetString());
        Assert.Equal(baseline.GetProperty("application_state").GetRawText(), status.GetProperty("application_state").GetRawText());
    }

    [LiveCorelFact]
    public async Task DuplicationThresholdCountsGroupDescendantsAndKeepsCopyOrder()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid); var doc = await c.Create();
        var (group, _, _) = await CreateCard(c, doc);
        var copies = await c.Draw(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 5, step_x_mm = 90, step_y_mm = 0 });
        Assert.True(copies.TryGetProperty("execution_engine", out var copyEngine), "Group descendant count did not select the native route.");
        Assert.Equal("vba", copyEngine.GetString());
        Assert.Equal(20, copies.GetProperty("created_static_ids").GetArrayLength());
        Assert.Equal(5, copies.GetProperty("items").GetArrayLength());
        for (int index = 0; index < 5; index++)
        {
            var item = copies.GetProperty("items")[index]; Assert.Equal(7, item.GetProperty("type").GetInt32());
            Assert.Equal((index + 1) * 90, item.GetProperty("bounds_mm").GetProperty("left").GetDouble(), 3);
        }
        var final = await c.Inspect(doc);
        Assert.Equal(6, final.GetProperty("total_shapes").GetInt32());
        Assert.Equal(24, final.GetProperty("shapes").GetArrayLength());
    }

    [LiveCorelFact]
    public async Task GridRejectsLocksOverlapAndDescendantLimitBeforeMutation()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid); var doc = await c.Create();
        var (group, child, _) = await CreateCard(c, doc);
        async Task Rejected(object args, string code)
        {
            var before = await c.Inspect(doc); var failed = await c.DrawFailure(doc, "corel_grid_duplicate", args);
            Assert.Equal(code, failed.Error!.Code); Assert.False(failed.Error.Applied);
            Assert.Equal(before.GetRawText(), (await c.Inspect(doc)).GetRawText());
        }
        await Rejected(new { static_ids = new[] { group }, rows = 1, columns = 252, anchor_x_mm = 0, anchor_y_mm = 0 }, "RESULT_LIMIT_EXCEEDED");
        await Rejected(new { static_ids = new[] { group, child }, rows = 1, columns = 21, anchor_x_mm = 0, anchor_y_mm = 0 }, "OVERLAPPING_TARGETS");
        await c.Ok("test_lock_shape", new { document_id = doc, static_id = group, locked = true });
        await Rejected(new { static_ids = new[] { group }, rows = 1, columns = 21, anchor_x_mm = 0, anchor_y_mm = 0 }, "LOCKED_TARGET");
    }

    [LiveCorelFact]
    public async Task SmallGridDoesNotLoadNativeAndReadinessFailurePrecedesSourceMove()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid, () => throw new InvalidOperationException("Test unavailable native runtime."));
        var doc = await c.Create(); var page = await c.Inspect(doc);
        var layer = (await c.Draw(doc, "corel_manage_layers", new { action = "create", page_id = page.GetProperty("page_id").GetString(), name = "Readiness" })).GetProperty("layer").GetProperty("layer_id").GetString();
        int id = (await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 0, y_mm = 0, width_mm = 10, height_mm = 10 } } })).GetProperty("created_static_ids")[0].GetInt32();
        var single = await c.Draw(doc, "corel_grid_duplicate", new { static_ids = new[] { id }, rows = 1, columns = 1, anchor_x_mm = 0, anchor_y_mm = 0 });
        Assert.Empty(single.GetProperty("created_static_ids").EnumerateArray());
        var small = await c.Draw(doc, "corel_grid_duplicate", new { static_ids = new[] { id }, rows = 1, columns = 20, anchor_x_mm = 0, anchor_y_mm = 0 });
        Assert.False(small.TryGetProperty("execution_engine", out _)); Assert.Equal(19, small.GetProperty("created_static_ids").GetArrayLength());
        var before = await c.Inspect(doc);
        var failed = await c.DrawFailure(doc, "corel_grid_duplicate", new { static_ids = new[] { id }, rows = 1, columns = 21, anchor_x_mm = 30, anchor_y_mm = 20 });
        Assert.False(failed.Error!.Applied);
        Assert.Equal(before.GetRawText(), (await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialCloneWithoutLastItemRetainsItsWholeGroup(bool unknown)
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid, () => new CloneFaultRuntime(unknown ? "unknown" : "partial"));
        var doc = await c.Create(); var (group, _, _) = await CreateCard(c, doc); var before = await c.Inspect(doc);
        var failed = await c.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 5, step_x_mm = 90, step_y_mm = 0 });
        try
        {
            Assert.Equal(unknown ? "NATIVE_OUTCOME_UNKNOWN" : "TEST_PARTIAL", failed.Error!.Code);
            Assert.Equal(unknown ? (bool?)null : true, failed.Error.Applied);
            var details = JsonSerializer.SerializeToElement(failed.Result, Protocol.JsonOptions);
            Assert.Equal(8, details.GetProperty("created_static_ids").GetArrayLength());
            Assert.Empty(details.GetProperty("deleted_static_ids").EnumerateArray());
            var after = await c.Inspect(doc); Assert.Equal(12, after.GetProperty("shapes").GetArrayLength());
            var original = before.GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).ToHashSet();
            Assert.Equal(after.GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).Where(id => !original.Contains(id)).Order(),
                details.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).Order());
            if (unknown)
            {
                var blocked = await c.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 1, step_x_mm = 90, step_y_mm = 0 });
                Assert.Equal("NATIVE_SESSION_BLOCKED", blocked.Error!.Code);
                Assert.Equal(after.GetRawText(), (await c.Inspect(doc)).GetRawText());
            }
        }
        finally { if (unknown) await c.Ok("test_close_after_unknown", new { document_id = doc }); }
    }

    [LiveCorelFact]
    public async Task ExistingRootCannotBeAttributedAsNewAndPostcheckFailureBlocksReplay()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid, () => new CloneFaultRuntime("alias"));
        var doc = await c.Create(); var (group, _, _) = await CreateCard(c, doc);
        var failed = await c.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 5, step_x_mm = 90, step_y_mm = 0 });
        try
        {
            Assert.Equal("NATIVE_OUTCOME_UNKNOWN", failed.Error!.Code); Assert.Null(failed.Error.Applied);
            var details = JsonSerializer.SerializeToElement(failed.Result, Protocol.JsonOptions);
            Assert.Empty(details.GetProperty("created_static_ids").EnumerateArray());
            var after = await c.Inspect(doc); Assert.Equal(12, after.GetProperty("shapes").GetArrayLength());
            var blocked = await c.DrawFailure(doc, "corel_grid_duplicate", new { static_ids = new[] { group }, rows = 1, columns = 21, anchor_x_mm = 0, anchor_y_mm = 0 });
            Assert.Equal("NATIVE_SESSION_BLOCKED", blocked.Error!.Code);
            Assert.Equal(after.GetRawText(), (await c.Inspect(doc)).GetRawText());
        }
        finally { await c.Ok("test_close_after_unknown", new { document_id = doc }); }
    }

    [LiveCorelTheory]
    [InlineData("corrupt_text")]
    [InlineData("corrupt_style")]
    [InlineData("corrupt_geometry")]
    [InlineData("affected_alias")]
    [InlineData("extra_root")]
    public async Task CorruptedCopyOrIncompleteIdClaimsBlockFurtherMutations(string fault)
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid, () => new CloneFaultRuntime(fault));
        var doc = await c.Create(); var (group, _, _) = await CreateCard(c, doc); var before = await c.Inspect(doc);
        var failed = await c.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 5, step_x_mm = 90, step_y_mm = 0 });
        try
        {
            Assert.Equal("NATIVE_OUTCOME_UNKNOWN", failed.Error!.Code); Assert.Null(failed.Error.Applied);
            var details = JsonSerializer.SerializeToElement(failed.Result, Protocol.JsonOptions);
            var original = before.GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).ToHashSet();
            var after = await c.Inspect(doc);
            var observed = after.GetProperty("shapes").EnumerateArray().Select(s => s.GetProperty("static_id").GetInt32()).ToHashSet();
            foreach (var id in details.GetProperty("created_static_ids").EnumerateArray())
            {
                Assert.DoesNotContain(id.GetInt32(), original); Assert.Contains(id.GetInt32(), observed);
            }
            if (fault is "affected_alias" or "extra_root")
                Assert.Equal(observed.Except(original).Order(), details.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).Order());
            Assert.Empty(details.GetProperty("deleted_static_ids").EnumerateArray());
            var blocked = await c.DrawFailure(doc, "corel_duplicate_shapes", new { static_ids = new[] { group }, copies = 1, step_x_mm = 90, step_y_mm = 0 });
            Assert.Equal("NATIVE_SESSION_BLOCKED", blocked.Error!.Code);
            Assert.Equal(after.GetRawText(), (await c.Inspect(doc)).GetRawText());
        }
        finally { await c.Ok("test_close_after_unknown", new { document_id = doc }); }
    }

    [LiveCorelFact]
    public async Task NativeCopiesKeepEachSourcesOpaqueLayerId()
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid); var doc = await c.Create();
        var page = await c.Inspect(doc); var ids = new List<int>(); var layers = new List<string>();
        foreach (int x in new[] { 0, 30 })
        {
            var layer = (await c.Draw(doc, "corel_manage_layers", new { action = "create", page_id = page.GetProperty("page_id").GetString(), name = "Same name" })).GetProperty("layer").GetProperty("layer_id").GetString()!;
            var shape = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = x, y_mm = 0, width_mm = 10, height_mm = 10 } } });
            ids.Add(shape.GetProperty("created_static_ids")[0].GetInt32()); layers.Add(layer);
        }
        var made = await c.Draw(doc, "corel_duplicate_shapes", new { static_ids = ids, copies = 10, step_x_mm = 50, step_y_mm = 0 });
        Assert.Equal("vba", made.GetProperty("execution_engine").GetString());
        for (int index = 0; index < 20; index++)
            Assert.Equal(layers[index % 2], made.GetProperty("items")[index].GetProperty("layer_id").GetString());
    }

    [LiveCorelTheory]
    [InlineData("mm")]
    [InlineData("inch")]
    public async Task NestedGridKeepsOutlineBoundsWithShiftedOrigin(string unit)
    {
        using var c = new DrawingClient(ExecutionMode.Hybrid); var doc = await c.Create();
        await c.Ok("test_configure", new { document_id = doc, unit });
        var (inner, background, layer) = await CreateCard(c, doc);
        await c.Draw(doc, "corel_style_shapes", new { static_ids = new[] { background }, outline = new { kind = "uniform", width_mm = 1, color = new { model = "rgb", r = 0, g = 0, b = 0 } } });
        var ellipse = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "ellipse", x_mm = 60, y_mm = 30, width_mm = 5, height_mm = 5 } } });
        var group = await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { inner, ellipse.GetProperty("created_static_ids")[0].GetInt32() } });
        int root = group.GetProperty("created_static_ids")[0].GetInt32();
        var grid = await c.Draw(doc, "corel_grid_duplicate", new { static_ids = new[] { root }, rows = 3, columns = 4, gap_x_mm = 3, gap_y_mm = 4, anchor_x_mm = 5, anchor_y_mm = 7 });
        Assert.Equal("vba", grid.GetProperty("execution_engine").GetString());
        Assert.Equal(66, grid.GetProperty("created_static_ids").GetArrayLength());
        for (int index = 0; index < 12; index++)
        {
            var b = grid.GetProperty("actual_bounds")[index];
            Assert.Equal(5 + index % 4 * 89, b.GetProperty("x_mm").GetDouble(), 3);
            Assert.Equal(7 + index / 4 * 55, b.GetProperty("y_mm").GetDouble(), 3);
            Assert.Equal(86, b.GetProperty("width_mm").GetDouble(), 3);
            Assert.Equal(51, b.GetProperty("height_mm").GetDouble(), 3);
        }
        var final = await c.Inspect(doc);
        Assert.Equal(72, final.GetProperty("shapes").GetArrayLength());
        Assert.Equal(12, final.GetProperty("shapes").EnumerateArray().Count(s => s.GetProperty("depth").GetInt32() == 1 && s.GetProperty("type").GetInt32() == 7));
        Assert.Equal(12, final.GetProperty("shapes").EnumerateArray().Count(s => Text(s) == "NOVA"));
    }

    private sealed class CloneFaultRuntime(string fault) : INativeVbaRuntime
    {
        private readonly NativeVbaRuntime actual = new(DrawingClient.NativeDirectory);
        public NativeModuleIdentity? LastIdentity => actual.LastIdentity;
        public NativeModuleIdentity EnsureReady(OperationContext c) => actual.EnsureReady(c);
        public void RequireOperation(string operation) => actual.RequireOperation(operation);
        public void ValidateFonts(OperationContext c, string[] names) => actual.ValidateFonts(c, names);
        public string ExtractGeometry(OperationContext c, VGCore.Document doc, VGCore.ShapeRange targets, int[] ids, double mm, string nonce) => actual.ExtractGeometry(c, doc, targets, ids, mm, nonce);
        public NativeBatchReply Apply(PreparedNativeBatch batch)
        {
            if (batch.Operation != "create_batch" || (int)batch.Rows[0, 1] != 5) return actual.Apply(batch);
            var rows = new object[2, batch.Rows.GetLength(1)];
            for (int i = 0; i < 2; i++) for (int j = 0; j < rows.GetLength(1); j++) rows[i, j] = batch.Rows[i, j];
            var reply = actual.Apply(batch with { Rows = rows, Keys = batch.Keys[..2] });
            if (fault == "alias") return reply with { Ok = false, Partial = true, CreatedStaticIds = [(int)batch.Rows[0, 26]], AffectedStaticIds = [(int)batch.Rows[0, 26]], Items = [], ErrorCode = "TEST_ALIAS", ErrorMessage = "Test existing root reported as a copy." };
            if (fault.StartsWith("corrupt_", StringComparison.Ordinal))
            {
                var root = CorelMcp.Worker.Operations.ShapeTraversal.Resolve(batch.Context, batch.Page, [reply.CreatedStaticIds[1]])[0];
                var text = CorelMcp.Worker.Operations.ShapeTraversal.Walk(batch.Context, batch.Context.Track(root.Shapes))
                    .Select(item => item.Shape).First(shape => shape.Type == VGCore.cdrShapeType.cdrTextShape);
                if (fault == "corrupt_text") batch.Context.Track(batch.Context.Track(text.Text).Story).WideText = "DAMAGED";
                if (fault == "corrupt_style") batch.Context.Track(batch.Context.Track(text.Fill).UniformColor).RGBAssign(255, 0, 0);
                if (fault == "corrupt_geometry") text.Move(batch.Factor, 0);
            }
            if (fault == "extra_root") batch.Context.Track(batch.Context.Track(batch.Targets[1]).Duplicate(batch.Factor * 270, 0));
            if (fault == "affected_alias") return reply with { Ok = false, Partial = true, AffectedStaticIds = reply.AffectedStaticIds.Append((int)batch.Rows[0, 26]).ToArray(), ErrorCode = "TEST_PARTIAL", ErrorMessage = "Test additional existing affected ID." };
            return reply with { Ok = false, Applied = fault == "unknown" ? null : true, Partial = true, Items = reply.Items[..1], ErrorCode = "TEST_PARTIAL", ErrorMessage = "Test partial clone reply without the last item." };
        }
        public void Dispose() => actual.Dispose();
    }

    private static string? Text(JsonElement shape) => shape.TryGetProperty("text", out var text) ? text.GetString() : null;
    private static async Task<(int Group, int Child, string Layer)> CreateCard(DrawingClient c, string doc)
    {
        var page = await c.Inspect(doc);
        var layer = (await c.Draw(doc, "corel_manage_layers", new { action = "create", page_id = page.GetProperty("page_id").GetString(), name = "Card" })).GetProperty("layer").GetProperty("layer_id").GetString()!;
        var made = await c.Draw(doc, "corel_create_batch", new { layer_id = layer, items = new object[] {
            new { item_key = "background", kind = "rectangle", x_mm = 0, y_mm = 0, width_mm = 85, height_mm = 50, fill = new { kind = "uniform", color = new { model = "rgb", r = 22, g = 30, b = 40 } }, outline = new { kind = "none" } },
            new { item_key = "title", kind = "artistic_text", text = "NOVA", x_mm = 6, y_mm = 30, font_family = "Arial", font_size_pt = 18, outline = new { kind = "none" } },
            new { item_key = "subtitle", kind = "artistic_text", text = "Design studio", x_mm = 6, y_mm = 13, font_family = "Arial", font_size_pt = 10, outline = new { kind = "none" } }
        } });
        var ids = made.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var group = await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = ids });
        return (group.GetProperty("created_static_ids")[0].GetInt32(), ids[0], layer);
    }
}
