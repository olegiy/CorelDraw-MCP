using System.Text.Json;
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
            // 20→29 at 45°, 7→19 at 30°; angle 0 round-trips EdgePad exactly; conical is unaffected).
            Assert.Equal(15, fountain.GetProperty("edge_pad").GetInt32());
            Assert.Equal(50, fountain.GetProperty("mid_point").GetInt32());
            // Live-observed: CorelDRAW marks multi-stop fountains as custom blend regardless of the requested
            // blend_type; two-stop fills keep the requested blend (pinned in the defaults test below).
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
}
