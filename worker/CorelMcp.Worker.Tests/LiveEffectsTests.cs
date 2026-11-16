using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using CorelMcp.Worker.Hybrid;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveEffectsTests
{
    [LiveCorelFact]
    public async Task InspectionReportsAnEmptyEffectCollectionWithoutChangingTheDocument()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        var initial = await c.Inspect(doc);
        string layer = initial.GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 50, height_mm = 25 } } });
        var state = await c.Inspect(doc);
        var shape = state.GetProperty("shapes")[0];
        Assert.True(shape.TryGetProperty("effects", out var effects), "Inspection must report effects instead of leaving an unsupported-shape rejection unexplained.");
        Assert.True(effects.GetProperty("complete").GetBoolean());
        Assert.Equal(0, effects.GetProperty("count").GetInt32());
        Assert.Empty(effects.GetProperty("items").EnumerateArray());
        Assert.Equal(state.GetRawText(), (await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task InspectionDistinguishesUniformTransparencyFromAnOrdinaryLens()
    {
        using var c = new LiveDrawingTests.DrawingClient();
        var doc = await c.Create();
        string layer = (await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 50, height_mm = 25 } } });
        int id = made.GetProperty("items")[0].GetProperty("static_id").GetInt32();
        await c.Ok("test_uniform_transparency", new { document_id = doc, static_ids = new[] { id }, value = 50 });
        var state = await c.Inspect(doc);
        var effects = state.GetProperty("shapes")[0].GetProperty("effects");
        Assert.True(effects.GetProperty("complete").GetBoolean());
        Assert.Equal(1, effects.GetProperty("count").GetInt32());
        var effect = effects.GetProperty("items")[0];
        Assert.Equal((int)VGCore.cdrEffectType.cdrLens, effect.GetProperty("type").GetInt32());
        Assert.True(effect.TryGetProperty("transparency", out var transparency), "The lens collection also contains ordinary object transparency; inspection must distinguish it.");
        Assert.Equal((int)VGCore.cdrTransparencyType.cdrUniformTransparency, transparency.GetProperty("type").GetInt32());
        Assert.Equal(50, transparency.GetProperty("uniform").GetInt32());
        Assert.False(transparency.GetProperty("frozen").GetBoolean());
        Assert.Equal(state.GetRawText(), (await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task RasterStickerAssembliesPreserveTransparencyWhenCopiedGroupedMovedAndRotated()
    {
        foreach (var mode in new[] { ExecutionMode.CSharp, ExecutionMode.Hybrid })
        foreach (var unit in new[] { "mm", "inch" })
        foreach (var fountain in new[] { false, true })
        {
            using var c = new LiveDrawingTests.DrawingClient(mode);
            var doc = await c.Create();
            await c.Ok("test_configure", new { document_id = doc, unit });
            var control = await c.Create(); var controlBefore = await c.Inspect(control);
            string layer = (await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            string path = Path.Combine(c.Directory, "sticker.jpg");
            using (var image = new Bitmap(200, 100))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Blue);
                graphics.FillRectangle(Brushes.Red, 0, 0, 100, 100);
                image.Save(path, ImageFormat.Jpeg);
            }
            var imported = await c.Draw(doc, "corel_import", new { path, format = "jpeg", sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), size_bytes = new FileInfo(path).Length,
                layer_id = layer, x_mm = 10, y_mm = 10, fit = new { width_mm = 50, height_mm = 25 } });
            int bitmap = imported.GetProperty("imported_root_static_ids")[0].GetInt32();
            var overlay = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 15, y_mm = 15, width_mm = 40, height_mm = 10 } } });
            int transparent = overlay.GetProperty("items")[0].GetProperty("static_id").GetInt32();
            await c.Draw(doc, "corel_style_shapes", new { static_ids = new[] { transparent }, fill = new { kind = "uniform", color = new { model = "rgb", r = 255, g = 255, b = 255 } }, outline = new { kind = "none" } });
            transparent = (await c.Draw(doc, "corel_convert_to_curves", new { static_ids = new[] { transparent } })).GetProperty("items")[0].GetProperty("static_id").GetInt32();
            await c.Ok(fountain ? "test_fountain_transparency" : "test_uniform_transparency", new { document_id = doc, static_ids = new[] { transparent }, value = 50 });
            var before = await c.Inspect(doc);
            var effectBefore = before.GetProperty("shapes").EnumerateArray().Single(x => x.GetProperty("static_id").GetInt32() == transparent).GetProperty("effects");
            Assert.Equal(1, effectBefore.GetProperty("count").GetInt32());
            Assert.Equal(fountain ? 2 : 1, effectBefore.GetProperty("items")[0].GetProperty("transparency").GetProperty("type").GetInt32());
            var duplicate = await c.Draw(doc, "corel_duplicate_shapes", new { static_ids = new[] { bitmap, transparent }, copies = 1, step_x_mm = 60, step_y_mm = 0 });
            int[] copies = duplicate.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
            Assert.Equal(2, copies.Length);
            var state = await c.Inspect(doc);
            Assert.Equal(effectBefore.GetRawText(), state.GetProperty("shapes").EnumerateArray().Single(x => x.GetProperty("static_id").GetInt32() == copies[1]).GetProperty("effects").GetRawText());
            var points = new[] { (20.0, 12.0), (45.0, 12.0), (20.0, 20.0), (45.0, 20.0) };
            var samples = new List<Color>();
            var preview = await c.Ok("corel_preview", new { document_id = doc, max_size = 1200 });
            using (var image = new Bitmap(preview.GetProperty("path").GetString()!))
            {
                foreach (var p in points)
                {
                    var a = image.GetPixel((int)(image.Width * p.Item1 / 200), (int)(image.Height * (1 - p.Item2 / 150)));
                    var b = image.GetPixel((int)(image.Width * (p.Item1 + 60) / 200), (int)(image.Height * (1 - p.Item2 / 150)));
                    samples.Add(a);
                    Assert.InRange(Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B), 0, 6);
                }
                var red = image.GetPixel((int)(image.Width * 20.0 / 200), (int)(image.Height * (1 - 12.0 / 150)));
                var blue = image.GetPixel((int)(image.Width * 45.0 / 200), (int)(image.Height * (1 - 12.0 / 150)));
                Assert.True(red.R > 200 && red.B < 40 && blue.B > 200 && blue.R < 40);
            }
            var group = await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = copies });
            int root = group.GetProperty("items")[0].GetProperty("static_id").GetInt32();
            var page = (await c.Inspect(doc)).GetProperty("page_id").GetString();
            string targetLayer = (await c.Draw(doc, "corel_manage_layers", new { action = "create", page_id = page, name = "Sticker copies" })).GetProperty("layer").GetProperty("layer_id").GetString()!;
            await c.Draw(doc, "corel_manage_layers", new { action = "move_shapes", layer_id = targetLayer, static_ids = new[] { root } });
            await c.Draw(doc, "corel_transform_shapes", new { action = "move", static_ids = new[] { root }, dx_mm = -60, dy_mm = 50 });
            await c.Draw(doc, "corel_transform_shapes", new { action = "rotate", static_ids = new[] { root }, angle_deg = 90, anchor_x_mm = 35, anchor_y_mm = 72.5 });
            var grid = await c.Draw(doc, "corel_grid_duplicate", new { static_ids = new[] { root }, rows = 2, columns = 3, anchor_x_mm = 10, anchor_y_mm = 40, gap_x_mm = 0, gap_y_mm = 0 });
            Assert.Equal(6, grid.GetProperty("actual_bounds").GetArrayLength());
            foreach (var item in grid.GetProperty("actual_bounds").EnumerateArray())
            {
                Assert.InRange(item.GetProperty("width_mm").GetDouble(), 24.999, 25.001);
                Assert.InRange(item.GetProperty("height_mm").GetDouble(), 49.999, 50.001);
            }
            state = await c.Inspect(doc);
            Assert.Equal(7, state.GetProperty("shapes").EnumerateArray().Count(x => x.GetProperty("effects").GetProperty("count").GetInt32() == 1));
            async Task AssertRenderedCopies()
            {
                var rotatedPreview = await c.Ok("corel_preview", new { document_id = doc, max_size = 1200 });
                using var image = new Bitmap(rotatedPreview.GetProperty("path").GetString()!);
                for (int row = 0; row < 2; row++)
                for (int column = 0; column < 3; column++)
                for (int sample = 0; sample < points.Length; sample++)
                {
                    var p = points[sample];
                    double x = 10 + 25 - (p.Item2 - 10) + column * 25, y = 40 + (p.Item1 - 10) + row * 50;
                    var actual = image.GetPixel((int)(image.Width * x / 200), (int)(image.Height * (1 - y / 150)));
                    var expected = samples[sample];
                    Assert.InRange(Math.Abs(actual.R - expected.R) + Math.Abs(actual.G - expected.G) + Math.Abs(actual.B - expected.B), 0, 6);
                }
            }
            await AssertRenderedCopies();
            await c.Draw(doc, "corel_group_shapes", new { action = "ungroup", static_ids = new[] { root } });
            state = await c.Inspect(doc);
            Assert.DoesNotContain(state.GetProperty("shapes").EnumerateArray(), x => x.GetProperty("static_id").GetInt32() == root);
            Assert.Equal(7, state.GetProperty("shapes").EnumerateArray().Count(x => x.GetProperty("effects").GetProperty("count").GetInt32() == 1));
            Assert.Equal(7, state.GetProperty("shapes").EnumerateArray().Count(x => x.GetProperty("type").GetInt32() == 5));
            await AssertRenderedCopies();
            foreach (var id in new[] { bitmap, transparent })
            {
                var old = before.GetProperty("shapes").EnumerateArray().Single(x => x.GetProperty("static_id").GetInt32() == id);
                var current = state.GetProperty("shapes").EnumerateArray().Single(x => x.GetProperty("static_id").GetInt32() == id);
                Assert.Equal(old.GetRawText(), current.GetRawText());
            }
            Assert.Equal(controlBefore.GetRawText(), (await c.Inspect(control)).GetRawText());
        }
    }

    [LiveCorelFact]
    public async Task GroupedFountainTransparencyDoesNotEnterVbaAboveTheNativeThreshold()
    {
        foreach (var grid in new[] { false, true })
        {
            using var c = new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid, () => throw new InvalidOperationException("Transparent group descendants must not enter VBA."));
            var doc = await c.Create();
            string layer = (await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 5, height_mm = 5 }, new { kind = "rectangle", x_mm = 15, y_mm = 10, width_mm = 5, height_mm = 5 } } });
            int[] ids = made.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
            await c.Ok("test_fountain_transparency", new { document_id = doc, static_ids = new[] { ids[0] } });
            int root = (await c.Draw(doc, "corel_group_shapes", new { action = "group", static_ids = ids })).GetProperty("items")[0].GetProperty("static_id").GetInt32();
            var result = grid
                ? await c.Call("corel_grid_duplicate", new { request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = new[] { root }, rows = 1, columns = 21, anchor_x_mm = 0, anchor_y_mm = 0 })
                : await c.Call("corel_duplicate_shapes", new { request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = new[] { root }, copies = 20, step_x_mm = 10, step_y_mm = 0 });
            Assert.True(result.Ok, JsonSerializer.Serialize(result, Protocol.JsonOptions));
            Assert.Equal(true, result.Cleanup!["completed"]);
            Assert.Equal("csharp", result.Versions!["execution_engine"]);
            var state = await c.Inspect(doc);
            Assert.Equal(21, state.GetProperty("total_shapes").GetInt32());
            Assert.Equal(63, state.GetProperty("shapes").GetArrayLength());
            var transparent = state.GetProperty("shapes").EnumerateArray().Where(x => x.GetProperty("effects").GetProperty("count").GetInt32() == 1).ToArray();
            Assert.Equal(21, transparent.Length);
            foreach (var shape in transparent) Assert.Equal(2, shape.GetProperty("effects").GetProperty("items")[0].GetProperty("transparency").GetProperty("type").GetInt32());
        }
    }

    [LiveCorelFact]
    public async Task TransparentBitmapAndVectorCopiesStayOnCSharpAboveTheNativeThreshold()
    {
        foreach (var bitmap in new[] { false, true })
        {
            using var c = new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid, () => throw new InvalidOperationException("Transparency must not enter VBA before its content is verified."));
            var doc = await c.Create();
            string layer = (await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            int id;
            if (bitmap)
            {
                string path = Path.Combine(c.Directory, "transparent-bitmap.png");
                using (var image = new Bitmap(20, 10)) { using var graphics = Graphics.FromImage(image); graphics.Clear(Color.Blue); image.Save(path, ImageFormat.Png); }
                var imported = await c.Draw(doc, "corel_import", new { path, format = "png", sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), size_bytes = new FileInfo(path).Length,
                    layer_id = layer, x_mm = 10, y_mm = 10, fit = new { width_mm = 5, height_mm = 5 } });
                id = imported.GetProperty("imported_root_static_ids")[0].GetInt32();
            }
            else id = (await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 5, height_mm = 5 } } })).GetProperty("items")[0].GetProperty("static_id").GetInt32();
            await c.Ok("test_uniform_transparency", new { document_id = doc, static_ids = new[] { id }, value = 50 });
            foreach (int count in new[] { 19, 20 })
            {
                var copied = await c.Call("corel_duplicate_shapes", new { request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = new[] { id }, copies = count, step_x_mm = 6, step_y_mm = 0 });
                Assert.True(copied.Ok, JsonSerializer.Serialize(copied, Protocol.JsonOptions));
                Assert.Equal(true, copied.Cleanup!["completed"]);
                Assert.Equal("csharp", copied.Versions!["execution_engine"]);
                var result = JsonSerializer.SerializeToElement(copied.Result, Protocol.JsonOptions);
                Assert.Equal(count, result.GetProperty("items").GetArrayLength());
                var grid = await c.Call("corel_grid_duplicate", new { request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = new[] { id }, rows = 1, columns = count + 1, anchor_x_mm = 10, anchor_y_mm = 30 });
                Assert.True(grid.Ok, JsonSerializer.Serialize(grid, Protocol.JsonOptions));
                Assert.Equal(true, grid.Cleanup!["completed"]);
                Assert.Equal("csharp", grid.Versions!["execution_engine"]);
            }
            var state = await c.Inspect(doc);
            int[] ids = state.GetProperty("shapes").EnumerateArray().Select(x => x.GetProperty("static_id").GetInt32()).ToArray();
            var moved = await c.Call("corel_transform_shapes", new { request_id = Guid.NewGuid().ToString(), document_id = doc, page_index = 1, static_ids = ids, action = "move", dx_mm = 1, dy_mm = 1 });
            Assert.True(moved.Ok, JsonSerializer.Serialize(moved, Protocol.JsonOptions));
            Assert.Equal("csharp", moved.Versions!["execution_engine"]);
            state = await c.Inspect(doc);
            Assert.Equal(79, state.GetProperty("total_shapes").GetInt32());
            foreach (var shape in state.GetProperty("shapes").EnumerateArray())
            {
                var effect = shape.GetProperty("effects").GetProperty("items")[0];
                Assert.Equal(50, effect.GetProperty("transparency").GetProperty("uniform").GetInt32());
            }
        }
    }

    [LiveCorelFact]
    public async Task UnsupportedLensAndVectorOnlyOperationsStillRejectBeforeMutation()
    {
        using var c = new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc = await c.Create();
        string layer = (await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(x => !x.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 50, height_mm = 25 }, new { kind = "rectangle", x_mm = 70, y_mm = 10, width_mm = 50, height_mm = 25 } } });
        int id = made.GetProperty("items")[0].GetProperty("static_id").GetInt32(), other = made.GetProperty("items")[1].GetProperty("static_id").GetInt32();
        await c.Ok("test_uniform_transparency", new { document_id = doc, static_ids = new[] { id }, value = 50 });
        async Task Reject(string tool, object args, string code = "UNSUPPORTED_SHAPE")
        {
            var before = await c.Inspect(doc);
            var failure = await c.DrawFailure(doc, tool, args);
            Assert.Equal(code, failure.Error!.Code);
            Assert.False(failure.Error.Applied);
            Assert.Equal(before.GetRawText(), (await c.Inspect(doc)).GetRawText());
        }
        await Reject("corel_style_shapes", new { static_ids = new[] { id }, outline = new { kind = "none" } });
        await Reject("corel_convert_to_curves", new { static_ids = new[] { id } });
        await Reject("corel_boolean_shapes", new { action = "weld", left_id = id, right_id = other, keep_left = true, keep_right = true });
        await Reject("corel_transform_shapes", new { action = "scale", static_ids = new[] { id }, sx = 2, sy = 2, anchor_x_mm = 0, anchor_y_mm = 0 });
        await Reject("corel_layout_shapes", new { static_ids = new[] { id }, dry_run = true });
        await Reject("corel_update_batch", new { items = new[] { new { static_id = id, transform = new { action = "move", dx_mm = 1, dy_mm = 1 } } } });
        await c.Ok("test_magnify_lens", new { document_id = doc, static_id = other });
        var lensShape = (await c.Inspect(doc)).GetProperty("shapes").EnumerateArray().Single(x => x.GetProperty("static_id").GetInt32() == other);
        Assert.Equal(0, lensShape.GetProperty("effects").GetProperty("items")[0].GetProperty("transparency").GetProperty("type").GetInt32());
        await Reject("corel_duplicate_shapes", new { static_ids = new[] { id, other }, copies = 1, step_x_mm = 0, step_y_mm = 30 });
        await Reject("corel_grid_duplicate", new { static_ids = new[] { id, other }, rows = 1, columns = 2, anchor_x_mm = 0, anchor_y_mm = 0 });
    }

    [Theory]
    [InlineData(VGCore.cdrTransparencyType.cdrUniformTransparency, false, false, false, true)]
    [InlineData(VGCore.cdrTransparencyType.cdrFountainTransparency, false, false, false, true)]
    [InlineData(VGCore.cdrTransparencyType.cdrNoTransparency, false, false, false, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrPatternTransparency, false, false, false, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrTextureTransparency, false, false, false, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrUniformTransparency, true, false, false, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrFountainTransparency, false, true, false, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrUniformTransparency, false, false, true, false)]
    [InlineData(VGCore.cdrTransparencyType.cdrFountainTransparency, false, false, true, false)]
    [InlineData((VGCore.cdrTransparencyType)999, false, false, false, false)]
    public void FrozenNonlocalAndUnknownTransparencyIsRejectedWithoutCreatingUnsafeLiveFixtures(VGCore.cdrTransparencyType type, bool frozen, bool lensFrozen, bool usesViewpoint, bool expected)
        => Assert.Equal(expected, Operations.DrawingTarget.IsLocalTransparency(type, frozen, lensFrozen, usesViewpoint));
}
