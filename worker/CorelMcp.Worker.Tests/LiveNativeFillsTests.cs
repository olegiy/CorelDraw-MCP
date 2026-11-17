using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using CorelMcp.Worker.Hybrid;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveNativeFillsTests
{
    private static string NormalLayer(JsonElement inspection)
        => inspection.GetProperty("layers").EnumerateArray().First(layer => !layer.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;

    private static Dictionary<string, object> Rgb(int r, int g, int b) => new() { ["model"] = "rgb", ["r"] = r, ["g"] = g, ["b"] = b };
    private static Dictionary<string, object> Cmyk(int c, int m, int y, int k) => new() { ["model"] = "cmyk", ["c"] = c, ["m"] = m, ["y"] = y, ["k"] = k };
    private static Dictionary<string, object> Stop(int position, Dictionary<string, object> color) => new() { ["position"] = position, ["color"] = color };

    private static void AssertStop(JsonElement stops, int index, int position, string model, params int[] values)
    {
        var stop = stops[index];
        Assert.Equal(position, stop.GetProperty("position").GetInt32());
        var color = stop.GetProperty("color");
        Assert.Equal(model, color.GetProperty("model").GetString());
        var keys = model == "rgb" ? new[] { "r", "g", "b" } : new[] { "c", "m", "y", "k" };
        for (var part = 0; part < keys.Length; part++)
            Assert.Equal(values[part], color.GetProperty(keys[part]).GetInt32());
    }

    [LiveCorelFact]
    public async Task MultiStopFountainFillsApplyAndReadBackOnRectangleEllipseAndCurve()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var primitives = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 },
            new { kind = "ellipse", x_mm = 60, y_mm = 10, width_mm = 30, height_mm = 20 } } });
        var curve = await c.Draw(doc, "corel_create_curve", new { layer_id = layer, subpaths = new object[] { new {
            start = new { x_mm = 10, y_mm = 40 }, closed = true, segments = new object[] {
                new { kind = "line", to = new { x_mm = 40, y_mm = 40 } },
                new { kind = "line", to = new { x_mm = 25, y_mm = 60 } } } } } });
        int[] ids = [.. primitives.GetProperty("created_static_ids").EnumerateArray().Select(value => value.GetInt32()),
            curve.GetProperty("created_static_ids")[0].GetInt32()];
        Assert.Equal(3, ids.Length);

        var styled = await c.Draw(doc, "corel_style_shapes", new
        {
            static_ids = ids,
            fill = new
            {
                kind = "fountain", type = "linear",
                stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(50, Cmyk(0, 0, 0, 100)), Stop(100, Rgb(0, 0, 255)) },
                angle_deg = 45, spread = "pad", steps = 128, edge_pad = 0, mid_point = 50, blend_type = "direct"
            }
        });
        var items = styled.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(2, item.GetProperty("fill_type").GetInt32());
            var fountain = item.GetProperty("fountain");
            Assert.Equal("linear", fountain.GetProperty("type").GetString());
            Assert.Equal(45, fountain.GetProperty("angle_deg").GetDouble());
            Assert.Equal("pad", fountain.GetProperty("spread").GetString());
            Assert.Equal(128, fountain.GetProperty("steps").GetInt32());
            // Live-observed CorelDRAW renormalization, pinned per controller ruling: a linear fountain with a
            // non-zero angle re-derives EdgePad from the rotated axis (0→15 at 45° on this 40×20 mm rect;
            // 20→29 at 45°, 7→19 at 30°; angle 0 round-trips EdgePad exactly; conical is unaffected, square
            // behaves like linear — see the square probe below). Geometry hint: at 45° the rotated gradient axis
            // spans the rectangle's diagonal band, so Corel recomputes the edge pad from the wider projected
            // footprint instead of the unrotated box; the exact number scales with the fixture geometry.
            Assert.Equal(15, fountain.GetProperty("edge_pad").GetInt32());
            Assert.Equal(50, fountain.GetProperty("mid_point").GetInt32());
            // Live-observed: CorelDRAW marks multi-stop fountains as custom blend regardless of the requested
            // blend_type; two-stop fills keep the requested blend (pinned in the defaults test below). Geometry
            // hint: intermediate stops force a per-position color interpolation table, which Corel exposes only
            // through the custom blend path — read-back reflects that, not the requested direct blend.
            Assert.Equal("custom", fountain.GetProperty("blend_type").GetString());
            AssertStop(fountain.GetProperty("stops"), 0, 0, "rgb", 255, 0, 0);
            AssertStop(fountain.GetProperty("stops"), 1, 50, "cmyk", 0, 0, 0, 100);
            AssertStop(fountain.GetProperty("stops"), 2, 100, "rgb", 0, 0, 255);
        }
        var state = await c.Inspect(doc);
        var appearance = state.GetProperty("shapes").EnumerateArray().Single(shape => shape.GetProperty("static_id").GetInt32() == ids[0]).GetProperty("appearance");
        Assert.Equal(2, appearance.GetProperty("fill_type").GetInt32());
        Assert.Equal("linear", appearance.GetProperty("fountain").GetProperty("type").GetString());
        AssertStop(appearance.GetProperty("fountain").GetProperty("stops"), 1, 50, "cmyk", 0, 0, 0, 100);
        Assert.True(state.GetProperty("document").GetProperty("dirty").GetBoolean());
    }

    [LiveCorelFact]
    public async Task RadialFountainCenterOffsetsArePassedAsPercentInMmAndInchDocuments()
    {
        var unitCodes = new Dictionary<string, int>();
        foreach (var unit in new[] { "mm", "inch" })
        {
            using var c = new LiveDrawingTests.DrawingClient();
            var doc = await c.Create();
            await c.Ok("test_configure", new { document_id = doc, unit });
            var binding = await c.Inspect(doc);
            unitCodes[unit] = binding.GetProperty("document").GetProperty("unit").GetInt32();
            string layer = NormalLayer(binding);
            var created = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
                items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
            int id = created.GetProperty("created_static_ids")[0].GetInt32();
            var styled = await c.Draw(doc, "corel_style_shapes", new
            {
                static_ids = new[] { id },
                fill = new
                {
                    kind = "fountain", type = "radial",
                    stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(100, Rgb(0, 0, 255)) },
                    center_offset_x_percent = 20, center_offset_y_percent = -30
                }
            });
            var fountain = styled.GetProperty("items")[0].GetProperty("fountain");
            Assert.Equal("radial", fountain.GetProperty("type").GetString());
            Assert.InRange(fountain.GetProperty("center_offset_x_percent").GetDouble(), 19.999, 20.001);
            Assert.InRange(fountain.GetProperty("center_offset_y_percent").GetDouble(), -30.001, -29.999);
        }
        Assert.NotEqual(unitCodes["mm"], unitCodes["inch"]);
    }

    [LiveCorelFact]
    public async Task AbsentFountainOptionsRoundTripApiTypicalDefaults()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var created = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
            items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
        int id = created.GetProperty("created_static_ids")[0].GetInt32();
        var styled = await c.Draw(doc, "corel_style_shapes", new
        {
            static_ids = new[] { id },
            fill = new { kind = "fountain", type = "conical", stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(100, Rgb(0, 0, 255)) } }
        });
        var fountain = styled.GetProperty("items")[0].GetProperty("fountain");
        Assert.Equal("conical", fountain.GetProperty("type").GetString());
        Assert.Equal(0, fountain.GetProperty("angle_deg").GetDouble());
        Assert.Equal(0, fountain.GetProperty("center_offset_x_percent").GetDouble());
        Assert.Equal(0, fountain.GetProperty("center_offset_y_percent").GetDouble());
        // Defaults confirmed by live read-back: steps=256, edge_pad=0, mid_point=50, blend direct, spread pad, smooth_blend false.
        Assert.Equal(256, fountain.GetProperty("steps").GetInt32());
        Assert.Equal(0, fountain.GetProperty("edge_pad").GetInt32());
        Assert.Equal(50, fountain.GetProperty("mid_point").GetInt32());
        Assert.Equal("direct", fountain.GetProperty("blend_type").GetString());
        Assert.Equal("pad", fountain.GetProperty("spread").GetString());
        Assert.False(fountain.GetProperty("smooth_blend").GetBoolean());
    }

    [LiveCorelFact]
    public async Task FountainStyleStaysOnCSharpAboveTheNativeThresholdInHybridMode()
    {
        using var c = new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,
            () => throw new InvalidOperationException("Fountain fills must not enter the VBA row codec."));
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var ids = new List<int>();
        foreach (var _ in new[] { 0, 1 })
        {
            var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
                items = Enumerable.Range(0, 13).Select(index => new { kind = "rectangle", x_mm = index * 5, y_mm = 0, width_mm = 4, height_mm = 4 }).ToArray() });
            ids.AddRange(made.GetProperty("created_static_ids").EnumerateArray().Select(value => value.GetInt32()));
        }
        Assert.Equal(26, ids.Count);
        var styled = await c.Call("corel_style_shapes", new
        {
            request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = ids.ToArray(),
            fill = new { kind = "fountain", type = "linear", angle_deg = 30,
                stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(100, Rgb(0, 0, 255)) } }
        });
        Assert.True(styled.Ok, JsonSerializer.Serialize(styled, Protocol.JsonOptions));
        Assert.Equal(true, styled.Cleanup!["completed"]);
        Assert.Equal("csharp", styled.Versions!["execution_engine"]);
        var state = await c.Inspect(doc);
        Assert.Equal(26, state.GetProperty("shapes").EnumerateArray()
            .Count(shape => shape.GetProperty("appearance").GetProperty("fill_type").GetInt32() == 2));
    }

    [LiveCorelFact]
    public async Task UniformAndFountainTransparencyApplyReadBackAndRemoveThroughTheKindUnion()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
            items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
        int id = made.GetProperty("items")[0].GetProperty("static_id").GetInt32();

        // Pinned per review: transparency executes on the C# engine (no VBA row codec involved).
        var uniformCall = await c.Call("corel_set_transparency", new
        {
            request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1,
            static_ids = new[] { id }, kind = "uniform", value = 40, applied_to = "fill", merge_mode = "normal"
        });
        Assert.True(uniformCall.Ok, JsonSerializer.Serialize(uniformCall, Protocol.JsonOptions));
        Assert.Equal(true, uniformCall.Cleanup!["completed"]);
        Assert.Equal("csharp", uniformCall.Versions!["execution_engine"]);
        var uniform = JsonSerializer.SerializeToElement(uniformCall.Result, Protocol.JsonOptions);
        var item = uniform.GetProperty("items")[0];
        Assert.Equal(id, item.GetProperty("static_id").GetInt32());
        Assert.Empty(uniform.GetProperty("created_static_ids").EnumerateArray());
        Assert.Empty(uniform.GetProperty("deleted_static_ids").EnumerateArray());
        var transparency = item.GetProperty("effects").GetProperty("items")[0].GetProperty("transparency");
        Assert.Equal((int)VGCore.cdrTransparencyType.cdrUniformTransparency, transparency.GetProperty("type").GetInt32());
        Assert.Equal(40, transparency.GetProperty("uniform").GetInt32());
        Assert.Equal((int)VGCore.cdrTransparencyAppliedTo.cdrApplyToFill, transparency.GetProperty("applied_to").GetInt32());
        Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeNormal, transparency.GetProperty("merge_mode").GetInt32());
        Assert.False(transparency.GetProperty("frozen").GetBoolean());

        // Re-apply through the kind union replaces the lens; StaticID never changes.
        var fountain = await c.Draw(doc, "corel_set_transparency", new
        {
            static_ids = new[] { id }, kind = "fountain", start = 0, end = 100, type = "linear", angle_deg = 45,
            applied_to = "outline", merge_mode = "multiply"
        });
        transparency = fountain.GetProperty("items")[0].GetProperty("effects").GetProperty("items")[0].GetProperty("transparency");
        Assert.Equal((int)VGCore.cdrTransparencyType.cdrFountainTransparency, transparency.GetProperty("type").GetInt32());
        Assert.Equal((int)VGCore.cdrTransparencyAppliedTo.cdrApplyToOutline, transparency.GetProperty("applied_to").GetInt32());
        Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeMultiply, transparency.GetProperty("merge_mode").GetInt32());
        Assert.False(transparency.GetProperty("frozen").GetBoolean());
        Assert.Equal(id, fountain.GetProperty("items")[0].GetProperty("static_id").GetInt32());

        // kind none without the envelope: applied_to/merge_mode are optional for removal (defaults fill/normal,
        // values ignored); pinned live together with the empty effect collection read-back.
        var removed = await c.Draw(doc, "corel_set_transparency", new { static_ids = new[] { id }, kind = "none" });
        var effects = removed.GetProperty("items")[0].GetProperty("effects");
        Assert.Equal(0, effects.GetProperty("count").GetInt32());
        Assert.Empty(effects.GetProperty("items").EnumerateArray());

        var state = await c.Inspect(doc);
        Assert.Equal(0, state.GetProperty("shapes").EnumerateArray()
            .Single(shape => shape.GetProperty("static_id").GetInt32() == id).GetProperty("effects").GetProperty("count").GetInt32());
        Assert.True(state.GetProperty("document").GetProperty("dirty").GetBoolean());
    }

    [LiveCorelFact]
    public async Task FountainTransparencyAcceptsRadialCenterPercentsAndSquareAngle()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
            items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
        int id = made.GetProperty("items")[0].GetProperty("static_id").GetInt32();
        var radial = await c.Draw(doc, "corel_set_transparency", new
        {
            static_ids = new[] { id }, kind = "fountain", start = 0, end = 100, type = "radial",
            center_offset_x_percent = 20, center_offset_y_percent = -30, applied_to = "fill_and_outline", merge_mode = "screen"
        });
        var transparency = radial.GetProperty("items")[0].GetProperty("effects").GetProperty("items")[0].GetProperty("transparency");
        Assert.Equal((int)VGCore.cdrTransparencyType.cdrFountainTransparency, transparency.GetProperty("type").GetInt32());
        Assert.Equal((int)VGCore.cdrTransparencyAppliedTo.cdrApplyToFillAndOutline, transparency.GetProperty("applied_to").GetInt32());
        Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeScreen, transparency.GetProperty("merge_mode").GetInt32());
        var square = await c.Draw(doc, "corel_set_transparency", new
        {
            static_ids = new[] { id }, kind = "fountain", start = 10, end = 90, type = "square", angle_deg = -45,
            applied_to = "fill", merge_mode = "overlay"
        });
        transparency = square.GetProperty("items")[0].GetProperty("effects").GetProperty("items")[0].GetProperty("transparency");
        Assert.Equal((int)VGCore.cdrTransparencyType.cdrFountainTransparency, transparency.GetProperty("type").GetInt32());
        Assert.Equal((int)VGCore.cdrTransparencyAppliedTo.cdrApplyToFill, transparency.GetProperty("applied_to").GetInt32());
        Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeOverlay, transparency.GetProperty("merge_mode").GetInt32());
    }

    [LiveCorelFact]
    public async Task GroupTransparencyAppliesToEverySupportedLeafAndRefusesMixedGroupsBeforeMutation()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 20, height_mm = 10 },
            new { kind = "ellipse", x_mm = 40, y_mm = 10, width_mm = 15, height_mm = 10 } } });
        int[] ids = made.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
        int group = (await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = ids }))
            .GetProperty("items")[0].GetProperty("static_id").GetInt32();

        var applied = await c.Draw(doc, "corel_set_transparency", new { static_ids = new[] { group }, kind = "uniform", value = 30, applied_to = "fill", merge_mode = "screen" });
        // Transparency is applied to every supported descendant leaf, never to the group node itself.
        Assert.Equal(2, applied.GetProperty("items").GetArrayLength());
        Assert.Equal(ids.ToHashSet(), applied.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("static_id").GetInt32()).ToHashSet());
        foreach (var leaf in applied.GetProperty("items").EnumerateArray())
        {
            var transparency = leaf.GetProperty("effects").GetProperty("items")[0].GetProperty("transparency");
            Assert.Equal((int)VGCore.cdrTransparencyType.cdrUniformTransparency, transparency.GetProperty("type").GetInt32());
            Assert.Equal(30, transparency.GetProperty("uniform").GetInt32());
            Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeScreen, transparency.GetProperty("merge_mode").GetInt32());
        }
        var state = await c.Inspect(doc);
        Assert.Equal(0, state.GetProperty("shapes").EnumerateArray()
            .Single(shape => shape.GetProperty("static_id").GetInt32() == group).GetProperty("effects").GetProperty("count").GetInt32());
        Assert.Equal(2, state.GetProperty("shapes").EnumerateArray()
            .Count(shape => shape.GetProperty("effects").GetProperty("count").GetInt32() == 1));

        var before = (await c.Inspect(doc)).GetRawText();
        var overlap = await c.DrawFailure(doc, "corel_set_transparency", new { static_ids = new[] { group, ids[0] }, kind = "uniform", value = 30, applied_to = "fill", merge_mode = "normal" });
        Assert.Equal("OVERLAPPING_TARGETS", overlap.Error!.Code);
        Assert.False(overlap.Error.Applied);
        Assert.Equal(before, (await c.Inspect(doc)).GetRawText());

        // A group containing a bitmap refuses the whole request before any mutation.
        string path = Path.Combine(c.Directory, "mixed.png");
        using (var image = new Bitmap(20, 10)) { using var graphics = Graphics.FromImage(image); graphics.Clear(Color.Blue); image.Save(path, ImageFormat.Png); }
        var imported = await c.Draw(doc, "corel_import", new { path, format = "png", sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), size_bytes = new FileInfo(path).Length,
            layer_id = layer, x_mm = 70, y_mm = 10, fit = new { width_mm = 10, height_mm = 5 } });
        int bitmap = imported.GetProperty("imported_root_static_ids")[0].GetInt32();
        var companion = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] { new { kind = "rectangle", x_mm = 85, y_mm = 10, width_mm = 10, height_mm = 5 } } });
        int fresh = companion.GetProperty("created_static_ids")[0].GetInt32();
        int mixed = (await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { fresh, bitmap } }))
            .GetProperty("items")[0].GetProperty("static_id").GetInt32();
        var mixedBefore = (await c.Inspect(doc)).GetRawText();
        var refused = await c.DrawFailure(doc, "corel_set_transparency", new { static_ids = new[] { mixed }, kind = "uniform", value = 30, applied_to = "fill", merge_mode = "normal" });
        Assert.Equal("UNSUPPORTED_SHAPE", refused.Error!.Code);
        Assert.False(refused.Error.Applied);
        Assert.Equal(mixedBefore, (await c.Inspect(doc)).GetRawText());
        // A direct bitmap target refuses through the same leaf validation.
        var bitmapDirect = await c.DrawFailure(doc, "corel_set_transparency", new { static_ids = new[] { bitmap }, kind = "uniform", value = 30, applied_to = "fill", merge_mode = "normal" });
        Assert.Equal("UNSUPPORTED_SHAPE", bitmapDirect.Error!.Code);
        Assert.False(bitmapDirect.Error.Applied);
        Assert.Equal(mixedBefore, (await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task TransparencyRefusalsProtectFrozenLockedLensAndBarrierTargetsBeforeMutation()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 50, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 90, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 130, y_mm = 10, width_mm = 30, height_mm = 15 } } });
        int[] ids = made.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();

        // Barriers preserved: style/scale/convert keep refusing transparent targets created by this tool.
        await c.Draw(doc, "corel_set_transparency", new { static_ids = new[] { ids[0] }, kind = "uniform", value = 40, applied_to = "fill", merge_mode = "normal" });
        async Task Reject(string tool, object args, string code = "UNSUPPORTED_SHAPE")
        {
            var before = await c.Inspect(doc);
            var failure = await c.DrawFailure(doc, tool, args);
            Assert.Equal(code, failure.Error!.Code);
            Assert.False(failure.Error.Applied);
            Assert.Equal(before.GetRawText(), (await c.Inspect(doc)).GetRawText());
        }
        await Reject("corel_style_shapes", new { static_ids = new[] { ids[0] }, outline = new { kind = "none" } });
        await Reject("corel_transform_shapes", new { action = "scale", static_ids = new[] { ids[0] }, sx = 2, sy = 2, anchor_x_mm = 0, anchor_y_mm = 0 });
        await Reject("corel_convert_to_curves", new { static_ids = new[] { ids[0] } });

        // Frozen lens: the tool itself freezes via freeze=true, then any re-application refuses before mutation.
        var frozen = await c.Draw(doc, "corel_set_transparency", new { static_ids = new[] { ids[1] }, kind = "uniform", value = 50, applied_to = "fill", merge_mode = "normal", freeze = true });
        Assert.True(frozen.GetProperty("items")[0].GetProperty("effects").GetProperty("items")[0].GetProperty("transparency").GetProperty("frozen").GetBoolean());
        await Reject("corel_set_transparency", new { static_ids = new[] { ids[1] }, kind = "none", applied_to = "fill", merge_mode = "normal" });

        await c.Ok("test_lock_shape", new { document_id = doc, static_id = ids[2], locked = true });
        await Reject("corel_set_transparency", new { static_ids = new[] { ids[2] }, kind = "uniform", value = 10, applied_to = "fill", merge_mode = "normal" }, "LOCKED_TARGET");

        await c.Ok("test_magnify_lens", new { document_id = doc, static_id = ids[3] });
        await Reject("corel_set_transparency", new { static_ids = new[] { ids[3] }, kind = "uniform", value = 10, applied_to = "fill", merge_mode = "normal" });

        // PowerClip containers keep their dedicated operation; transparency refuses before mutation.
        var clips = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 170, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 175, y_mm = 12, width_mm = 10, height_mm = 6 } } });
        int[] clipIds = clips.GetProperty("created_static_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        await c.Draw(doc, "corel_powerclip", new { action = "place", container_id = clipIds[0], static_ids = new[] { clipIds[1] } });
        await Reject("corel_set_transparency", new { static_ids = new[] { clipIds[0] }, kind = "uniform", value = 10, applied_to = "fill", merge_mode = "normal" });
    }

    [LiveCorelFact]
    public async Task SquareFountainFillEdgePadAndAngleBehaviorIsProbedLive()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
            items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
        int id = made.GetProperty("items")[0].GetProperty("static_id").GetInt32();
        // Probe for T7/T8: square fountain with a non-zero angle — does Corel renormalize EdgePad like linear?
        var rotated = await c.Draw(doc, "corel_style_shapes", new { static_ids = new[] { id }, fill = new {
            kind = "fountain", type = "square", angle_deg = 45, edge_pad = 20,
            stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(100, Rgb(0, 0, 255)) } } });
        var fountain = rotated.GetProperty("items")[0].GetProperty("fountain");
        Assert.Equal("square", fountain.GetProperty("type").GetString());
        Assert.Equal(45, fountain.GetProperty("angle_deg").GetDouble());
        // Live-observed CorelDRAW renormalization, pinned for T7/T8: square renormalizes EdgePad from the rotated
        // axis like linear (20→29 at 45° on the 40×20 mm rect — same value as the pinned linear case; conical is
        // unaffected). Geometry hint: the square fountain axis is aligned to the object box, so rotating it by 45°
        // widens the effective gradient band exactly as the linear axis does on this fixture.
        Assert.Equal(29, fountain.GetProperty("edge_pad").GetInt32());
        Assert.Equal("direct", fountain.GetProperty("blend_type").GetString());
        // Angle 0 round-trips EdgePad exactly (same pinned behavior as linear).
        var flat = await c.Draw(doc, "corel_style_shapes", new { static_ids = new[] { id }, fill = new {
            kind = "fountain", type = "square", angle_deg = 0, edge_pad = 20,
            stops = new object[] { Stop(0, Rgb(255, 0, 0)), Stop(100, Rgb(0, 0, 255)) } } });
        Assert.Equal(20, flat.GetProperty("items")[0].GetProperty("fountain").GetProperty("edge_pad").GetInt32());
        Assert.Equal(0, flat.GetProperty("items")[0].GetProperty("fountain").GetProperty("angle_deg").GetDouble());
    }

    private static object ShadowArgs(int[] staticIds, string kind = "shadow") => new
    {
        static_ids = staticIds, kind, type = "bottom", opacity = 80, feather = 15, offset_x_mm = 3, offset_y_mm = -4,
        color = new { model = "cmyk", c = 0, m = 0, y = 0, k = 100 },
        feather_type = "outside", feather_edge = "linear", merge_mode = "multiply", fade = 30
    };

    [LiveCorelFact]
    public async Task DropShadowAppliesToRectTextAndGroupMemberAndReportsIdMappings()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 },
            new { kind = "rectangle", x_mm = 60, y_mm = 10, width_mm = 15, height_mm = 10 },
            new { kind = "ellipse", x_mm = 85, y_mm = 10, width_mm = 15, height_mm = 10 } } });
        int[] ids = made.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
        int group = (await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { ids[1], ids[2] } }))
            .GetProperty("items")[0].GetProperty("static_id").GetInt32();
        var text = await c.Draw(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "Тень",
            x_mm = 10, y_mm = 60, font_family = "Arial", font_size_pt = 24 });
        int textId = text.GetProperty("created_static_ids")[0].GetInt32();
        var boundsBefore = (await c.Inspect(doc)).GetProperty("shapes").EnumerateArray()
            .Single(s => s.GetProperty("static_id").GetInt32() == ids[0]).GetProperty("bounds_mm");

        var call = await c.Call("corel_apply_drop_shadow", new
        {
            request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1,
            static_ids = new[] { ids[0], textId, ids[1] }, kind = "shadow", type = "bottom",
            opacity = 80, feather = 15, offset_x_mm = 3, offset_y_mm = -4,
            color = new { model = "cmyk", c = 0, m = 0, y = 0, k = 100 },
            feather_type = "outside", feather_edge = "linear", merge_mode = "multiply", fade = 30
        });
        Assert.True(call.Ok, JsonSerializer.Serialize(call, Protocol.JsonOptions));
        Assert.Equal("csharp", call.Versions!["execution_engine"]);
        var shadowed = JsonSerializer.SerializeToElement(call.Result, Protocol.JsonOptions);

        // Live-probed CorelDRAW 27 semantics (own temp docs, 2026-11-17): the target KEEPS its StaticID, type,
        // geometry and page/group position; the cdrDropShadow effect is attached to the target itself and a NEW
        // sibling cdrDropShadowGroupShape appears (shadow group + one shadow child). The design's wrap premise
        // (old id disappears) is NOT what the API does — pinned here as observed. id_mappings mirrors the
        // convert-to-curves report: source_static_id -> result_static_ids [target, group, child].
        int[] created = shadowed.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Empty(shadowed.GetProperty("deleted_static_ids").EnumerateArray());
        // Observed structure: exactly one shadow group with one shadow child per target.
        Assert.Equal(6, created.Length);
        var mappings = shadowed.GetProperty("id_mappings").EnumerateArray().ToArray();
        Assert.Equal(3, mappings.Length);
        foreach (int target in new[] { ids[0], textId, ids[1] })
        {
            var mapping = mappings.Single(m => m.GetProperty("source_static_id").GetInt32() == target);
            int[] resultIds = mapping.GetProperty("result_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(target, resultIds[0]);
            Assert.Equal(3, resultIds.Length);
            foreach (int createdId in resultIds.Skip(1)) Assert.Contains(createdId, created);
        }
        Assert.Equal(3, shadowed.GetProperty("items").GetArrayLength());
        var rect = shadowed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("static_id").GetInt32() == ids[0]);
        Assert.Equal(1, rect.GetProperty("type").GetInt32());
        Assert.Equal(boundsBefore.GetRawText(), rect.GetProperty("bounds_mm").GetRawText());
        var effects = rect.GetProperty("effects");
        Assert.Equal(1, effects.GetProperty("count").GetInt32());
        var effect = effects.GetProperty("items")[0];
        Assert.Equal((int)VGCore.cdrEffectType.cdrDropShadow, effect.GetProperty("type").GetInt32());
        var observed = effect.GetProperty("drop_shadow");
        Assert.Equal((int)VGCore.cdrDropShadowType.cdrDropShadowBottom, observed.GetProperty("type").GetInt32());
        Assert.Equal(80, observed.GetProperty("opacity").GetInt32());
        Assert.Equal(15, observed.GetProperty("feather").GetInt32());
        Assert.InRange(observed.GetProperty("offset_x_mm").GetDouble(), 2.999, 3.001);
        Assert.InRange(observed.GetProperty("offset_y_mm").GetDouble(), -4.001, -3.999);
        Assert.Equal((int)VGCore.cdrFeatherType.cdrFeatherOutside, observed.GetProperty("feather_type").GetInt32());
        Assert.Equal((int)VGCore.cdrEdgeType.cdrEdgeLinear, observed.GetProperty("feather_edge").GetInt32());
        Assert.Equal((int)VGCore.cdrMergeMode.cdrMergeMultiply, observed.GetProperty("merge_mode").GetInt32());
        Assert.Equal(30, observed.GetProperty("fade").GetInt32());
        // Perspective fields are not exposed in v1; Corel round-trips the passed zeros (observed defaults).
        Assert.Equal(0, observed.GetProperty("perspective_angle").GetDouble());
        Assert.Equal(0, observed.GetProperty("perspective_stretch").GetDouble());
        var textItem = shadowed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("static_id").GetInt32() == textId);
        Assert.Equal(6, textItem.GetProperty("type").GetInt32());
        Assert.Equal("Тень", textItem.GetProperty("text").GetString());
        Assert.Equal(1, textItem.GetProperty("effects").GetProperty("count").GetInt32());

        var state = await c.Inspect(doc);
        var rectState = state.GetProperty("shapes").EnumerateArray().Single(s => s.GetProperty("static_id").GetInt32() == ids[0]);
        Assert.Equal(1, rectState.GetProperty("type").GetInt32());
        Assert.Equal(1, rectState.GetProperty("effects").GetProperty("count").GetInt32());
        Assert.Equal(3, state.GetProperty("shapes").EnumerateArray().Count(s => s.GetProperty("type").GetInt32() == 16));
        // Group member: the shadow group is nested inside the parent group next to the shadowed member.
        Assert.Contains(state.GetProperty("shapes").EnumerateArray(),
            s => s.GetProperty("type").GetInt32() == 16 && s.GetProperty("parent_static_id").ValueKind == JsonValueKind.Number
                && s.GetProperty("parent_static_id").GetInt32() == group);
        var groupState = state.GetProperty("shapes").EnumerateArray().Single(s => s.GetProperty("static_id").GetInt32() == group);
        Assert.Equal(0, groupState.GetProperty("effects").GetProperty("count").GetInt32());
        Assert.True(state.GetProperty("document").GetProperty("dirty").GetBoolean());
    }

    [LiveCorelFact]
    public async Task DropShadowRefusalsProtectRepeatedGroupLockedBitmapAndExistingEffects()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new object[] {
            new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 50, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 90, y_mm = 10, width_mm = 30, height_mm = 15 },
            new { kind = "rectangle", x_mm = 130, y_mm = 10, width_mm = 30, height_mm = 15 } } });
        int[] ids = made.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
        int group = (await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = new[] { ids[2], ids[3] } }))
            .GetProperty("items")[0].GetProperty("static_id").GetInt32();
        string path = Path.Combine(c.Directory, "shadow-refusal.png");
        using (var image = new Bitmap(20, 10)) { using var graphics = Graphics.FromImage(image); graphics.Clear(Color.Blue); image.Save(path, ImageFormat.Png); }
        var imported = await c.Draw(doc, "corel_import", new { path, format = "png",
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), size_bytes = new FileInfo(path).Length,
            layer_id = layer, x_mm = 170, y_mm = 10, fit = new { width_mm = 10, height_mm = 5 } });
        int bitmap = imported.GetProperty("imported_root_static_ids")[0].GetInt32();

        var applied = await c.Draw(doc, "corel_apply_drop_shadow", ShadowArgs([ids[0]]));
        int[] shadowIds = applied.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Equal(2, shadowIds.Length);
        int shadowGroup = (await c.Inspect(doc)).GetProperty("shapes").EnumerateArray()
            .Single(s => s.GetProperty("type").GetInt32() == 16).GetProperty("static_id").GetInt32();

        async Task Reject(string tool, object args, string code = "UNSUPPORTED_SHAPE")
        {
            var snapshot = (await c.Inspect(doc)).GetRawText();
            var failure = await c.DrawFailure(doc, tool, args);
            Assert.Equal(code, failure.Error!.Code);
            Assert.False(failure.Error.Applied);
            Assert.Equal(snapshot, (await c.Inspect(doc)).GetRawText());
        }
        // Re-shadowing the already-shadowed target refuses before any mutation (no double shadow).
        await Reject("corel_apply_drop_shadow", ShadowArgs([ids[0]]));
        // Groups are a v1 documented limitation: a group target refuses before mutation.
        await Reject("corel_apply_drop_shadow", ShadowArgs([group]));
        // The shadow group itself is not a valid target; kind none removes via the original shape id.
        await Reject("corel_apply_drop_shadow", new { static_ids = new[] { shadowGroup }, kind = "none" });
        // The shadow child inside the effect group is not resolvable by its StaticID.
        await Reject("corel_apply_drop_shadow", ShadowArgs([shadowIds[1]]), "SHAPE_NOT_FOUND");
        // Bitmap targets refuse.
        await Reject("corel_apply_drop_shadow", ShadowArgs([bitmap]));
        // Locked targets refuse before mutation.
        await c.Ok("test_lock_shape", new { document_id = doc, static_id = ids[1], locked = true });
        await Reject("corel_apply_drop_shadow", ShadowArgs([ids[1]]), "LOCKED_TARGET");
        // kind none on a target without a drop shadow refuses before mutation.
        await Reject("corel_apply_drop_shadow", new { static_ids = new[] { ids[2] }, kind = "none" }, "NOT_A_DROP_SHADOW");
        // Existing barriers are untouched: the shadowed target keeps refusing style/transform/transparency/convert.
        await Reject("corel_style_shapes", new { static_ids = new[] { ids[0] }, outline = new { kind = "none" } });
        await Reject("corel_transform_shapes", new { action = "move", static_ids = new[] { ids[0] }, dx_mm = 5, dy_mm = 0 });
        await Reject("corel_set_transparency", new { static_ids = new[] { ids[0] }, kind = "uniform", value = 20, applied_to = "fill", merge_mode = "normal" });
        await Reject("corel_convert_to_curves", new { static_ids = new[] { ids[0] } });
    }

    [LiveCorelFact]
    public async Task DropShadowNoneRemovesTheEffectAndRestoresTheOriginalTarget()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = NormalLayer(await c.Inspect(doc));
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer,
            items = new object[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 40, height_mm = 20 } } });
        int rect = made.GetProperty("items")[0].GetProperty("static_id").GetInt32();
        var text = await c.Draw(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "Снять",
            x_mm = 60, y_mm = 30, font_family = "Arial", font_size_pt = 18 });
        int textId = text.GetProperty("created_static_ids")[0].GetInt32();

        // Live-probed typed removal path: Shape.ClearEffect(cdrDropShadow) deletes the effect together with the
        // whole shadow group and restores the target with its original StaticID and type.
        foreach (var (id, expectedType) in new[] { (rect, 1), (textId, 6) })
        {
            var applied = await c.Draw(doc, "corel_apply_drop_shadow", ShadowArgs([id]));
            int[] shadowIds = applied.GetProperty("created_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(2, shadowIds.Length);
            // kind none without merge_mode: the envelope is optional for removal and ignored.
            var removed = await c.Draw(doc, "corel_apply_drop_shadow", new { static_ids = new[] { id }, kind = "none" });
            var mapping = removed.GetProperty("id_mappings")[0];
            Assert.Equal(id, mapping.GetProperty("source_static_id").GetInt32());
            Assert.Equal(new[] { id }, mapping.GetProperty("result_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray());
            Assert.Equal(shadowIds.ToHashSet(), removed.GetProperty("deleted_static_ids").EnumerateArray().Select(v => v.GetInt32()).ToHashSet());
            Assert.Empty(removed.GetProperty("created_static_ids").EnumerateArray());
            var item = removed.GetProperty("items")[0];
            Assert.Equal(id, item.GetProperty("static_id").GetInt32());
            Assert.Equal(expectedType, item.GetProperty("type").GetInt32());
            Assert.Equal(0, item.GetProperty("effects").GetProperty("count").GetInt32());
            var state = await c.Inspect(doc);
            var shape = state.GetProperty("shapes").EnumerateArray().Single(s => s.GetProperty("static_id").GetInt32() == id);
            Assert.Equal(expectedType, shape.GetProperty("type").GetInt32());
            Assert.Equal(0, shape.GetProperty("effects").GetProperty("count").GetInt32());
            Assert.Equal(0, state.GetProperty("shapes").EnumerateArray().Count(s => s.GetProperty("type").GetInt32() == 16));
        }
        // Geometry survives the apply+remove round-trip.
        var restored = (await c.Inspect(doc)).GetProperty("shapes").EnumerateArray()
            .Single(s => s.GetProperty("static_id").GetInt32() == rect).GetProperty("bounds_mm");
        Assert.InRange(restored.GetProperty("width").GetDouble(), 39.999, 40.001);
        Assert.InRange(restored.GetProperty("height").GetDouble(), 19.999, 20.001);
        // Removal freed the effect slot: a fresh shadow applies again with new ids.
        var reapplied = await c.Draw(doc, "corel_apply_drop_shadow", ShadowArgs([rect]));
        Assert.Equal(2, reapplied.GetProperty("created_static_ids").EnumerateArray().Count());
        Assert.True((await c.Inspect(doc)).GetProperty("document").GetProperty("dirty").GetBoolean());
    }
}
