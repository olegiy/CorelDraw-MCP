using System.Text.Json;
using Xunit;
using DrawingClient = CorelMcp.Worker.Tests.LiveDrawingTests.DrawingClient;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveProductionTests
{
    [LiveCorelFact]
    public async Task PartialReplacementKeepsOneCurrentObservationPerShape()
    {
        using var c = new DrawingClient(); var doc = await c.Create(); var layer = Layer(await c.Inspect(doc));
        var created = await c.Draw(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "а а а а а", x_mm = 0, y_mm = 0, font_family = "Arial", font_size_pt = 12 });
        int id = created.GetProperty("created_static_ids")[0].GetInt32(); c.FailAfterMutations = 3;
        var partial = await c.DrawFailure(doc, "corel_edit_text", new { static_ids = new[] { id }, action = "replace_literal", old = "а", @new = "б", case_sensitive = true });
        Assert.True(partial.Error!.Applied);
        var result = JsonSerializer.SerializeToElement(partial.Result, Protocol.JsonOptions);
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal("а а б б б", item.GetProperty("text").GetString());
    }
    [LiveCorelFact]
    public async Task CubicInsertionPreservesGeometryAndRevisionsRejectStaleEdits()
    {
        foreach (var unit in new[] { "mm", "inch" })
        {
        using var c = new DrawingClient(); var doc = await c.Create(); var layer = Layer(await c.Inspect(doc));
        await c.Ok("test_configure", new { document_id = doc, unit });
        var created = await c.Draw(doc, "corel_create_curve", new { layer_id = layer, subpaths = new object[] {
            new { start = P(0,0), closed = false, segments = new object[] { new { kind = "cubic", to = P(10,0), control1 = P(0,10), control2 = P(10,10) } } },
            new { start = P(20,0), closed = true, segments = new object[] { new { kind = "line", to = P(30,0) }, new { kind = "line", to = P(25,10) } } } } });
        int id = created.GetProperty("created_static_ids")[0].GetInt32();
        async Task<JsonElement> Inspect(int limit = 100) => await c.Ok("corel_inspect_curve", new { document_id = doc, page_index = 1, static_id = id, limit });
        var before = await Inspect(); Assert.Equal(5, before.GetProperty("total_nodes").GetInt32()); Assert.Equal(4, before.GetProperty("total_segments").GetInt32());
        var revision = before.GetProperty("geometry_revision").GetString()!; Assert.Matches("^[a-f0-9]{64}$", revision);
        Assert.Equal(revision, (await Inspect(1)).GetProperty("geometry_revision").GetString());
        var inserted = await c.Draw(doc, "corel_edit_curve", new { static_id = id, expected_revision = revision, action = "insert_node", subpath_index = 1, segment_index = 1, t = 0.5 });
        var after = await Inspect(); Assert.NotEqual(revision, after.GetProperty("geometry_revision").GetString()); Assert.Equal(6, after.GetProperty("total_nodes").GetInt32());
        Point(after.GetProperty("nodes")[1].GetProperty("position"), 5, 7.5);
        Point(after.GetProperty("segments")[0].GetProperty("control1"), 0, 5); Point(after.GetProperty("segments")[0].GetProperty("control2"), 2.5, 7.5);
        Point(after.GetProperty("segments")[1].GetProperty("control1"), 7.5, 7.5); Point(after.GetProperty("segments")[1].GetProperty("control2"), 10, 5);
        var stale = await c.DrawFailure(doc, "corel_edit_curve", new { static_id = id, expected_revision = revision, action = "delete_node", subpath_index = 1, node_index = 2 });
        Assert.Equal("STALE_GEOMETRY", stale.Error!.Code); Assert.False(stale.Error.Applied); Assert.Equal(after.GetRawText(), (await Inspect()).GetRawText());
        await c.Draw(doc, "corel_edit_curve", new { static_id = id, expected_revision = after.GetProperty("geometry_revision").GetString(), action = "set_node_type", subpath_index = 1, node_index = 2, node_type = "cusp" });
        after = await Inspect(); Assert.Equal("cusp", after.GetProperty("nodes")[1].GetProperty("node_type").GetString());
        await c.Draw(doc, "corel_edit_curve", new { static_id = id, expected_revision = after.GetProperty("geometry_revision").GetString(), action = "move_node", subpath_index = 1, node_index = 3, x_mm = 12, y_mm = 0 });
        after = await Inspect(); Point(after.GetProperty("nodes")[2].GetProperty("position"), 12, 0);
        await c.Draw(doc, "corel_edit_curve", new { static_id = id, expected_revision = after.GetProperty("geometry_revision").GetString(), action = "delete_node", subpath_index = 1, node_index = 2 });
        after = await Inspect(); Assert.Equal(5, after.GetProperty("total_nodes").GetInt32());
        var invalid = await c.DrawFailure(doc, "corel_edit_curve", new { static_id = id, expected_revision = after.GetProperty("geometry_revision").GetString(), action = "delete_node", subpath_index = 1, node_index = 2 });
        Assert.Equal("MINIMUM_NODES", invalid.Error!.Code); Assert.False(invalid.Error.Applied);
        var inspection = (await c.Inspect(doc)).GetRawText(); await Inspect(); Assert.Equal(inspection, (await c.Inspect(doc)).GetRawText());
        var cannotClose = await c.DrawFailure(doc, "corel_edit_curve", new { static_id = id, expected_revision = after.GetProperty("geometry_revision").GetString(), action = "close_subpath", subpath_index = 1 });
        Assert.Equal("MINIMUM_NODES", cannotClose.Error!.Code); Assert.False(cannotClose.Error.Applied);
        }
    }
    [LiveCorelFact]
    public async Task JoinRequiresActualEndpointsWithinExplicitTolerance()
    {
        using var c = new DrawingClient(); var doc = await c.Create(); var layer = Layer(await c.Inspect(doc));
        var created = await c.Draw(doc, "corel_create_curve", new { layer_id = layer, subpaths = new object[] {
            new { start = P(0,0), closed = false, segments = new[] { new { kind = "line", to = P(10,0) } } },
            new { start = P(10.005,0), closed = false, segments = new[] { new { kind = "line", to = P(20,0) } } } } });
        int id = created.GetProperty("created_static_ids")[0].GetInt32();
        var geometry = await c.Ok("corel_inspect_curve", new { document_id = doc, page_index = 1, static_id = id });
        var revision = geometry.GetProperty("geometry_revision").GetString();
        var rejected = await c.DrawFailure(doc, "corel_edit_curve", new { static_id = id, expected_revision = revision, action = "join_endpoints", first_subpath_index = 1, first_end = "start", second_subpath_index = 2, second_end = "end", tolerance_mm = 0.01 });
        Assert.Equal("ENDPOINTS_TOO_FAR", rejected.Error!.Code); Assert.False(rejected.Error.Applied);
        await c.Draw(doc, "corel_edit_curve", new { static_id = id, expected_revision = revision, action = "join_endpoints", first_subpath_index = 1, first_end = "end", second_subpath_index = 2, second_end = "start", tolerance_mm = 0.01 });
        var joined = await c.Ok("corel_inspect_curve", new { document_id = doc, page_index = 1, static_id = id });
        Assert.Single(joined.GetProperty("subpaths").EnumerateArray()); Assert.Equal(3, joined.GetProperty("total_nodes").GetInt32());
    }
    [LiveCorelFact]
    public async Task LiteralReplacementPreservesUnmatchedFormattingAndSearchIsReadOnly()
    {
        using var c = new DrawingClient(); var doc = await c.Create(); var layer = Layer(await c.Inspect(doc));
        var text = await c.Draw(doc, "corel_create_text", new { kind = "artistic", layer_id = layer, text = "АА Ёж ББ Ёж", x_mm = 10, y_mm = 20, font_family = "Arial", font_size_pt = 12 });
        int id = text.GetProperty("created_static_ids")[0].GetInt32();
        await c.Ok("test_text_mixed", new { document_id = doc, static_id = id });
        await c.Draw(doc, "corel_edit_text", new { static_ids = new[] { id }, action = "replace_literal", old = "ёж", @new = "Лис", case_sensitive = false });
        var inspection = await c.Inspect(doc); Assert.Equal("АА Лис ББ Лис", inspection.GetProperty("shapes")[0].GetProperty("text").GetString());
        Assert.True((await c.Ok("test_text_character", new { document_id = doc, static_id = id, index = 1 })).GetProperty("bold").GetBoolean());
        Assert.True((await c.Ok("test_text_character", new { document_id = doc, static_id = id, index = 8 })).GetProperty("italic").GetBoolean());
        var found = await c.Ok("corel_find_text", new { document_id = doc, page_index = 1, query = "лис", case_sensitive = false });
        Assert.Equal(2, found.GetProperty("matches")[0].GetProperty("count").GetInt32()); Assert.False(found.GetProperty("matches")[0].TryGetProperty("snippet", out _));
        Assert.Equal(inspection.GetRawText(), (await c.Inspect(doc)).GetRawText());
        await c.Draw(doc, "corel_edit_text", new { static_ids = new[] { id }, action = "format", font_family = "Arial", font_size_pt = 15, bold = false, italic = true, alignment = "center", line_spacing_percent = 120 });
        var character = await c.Ok("test_text_character", new { document_id = doc, static_id = id, index = 1 });
        Assert.False(character.GetProperty("bold").GetBoolean()); Assert.True(character.GetProperty("italic").GetBoolean()); Assert.Equal(15, character.GetProperty("size").GetDouble());
        Assert.Equal(3, character.GetProperty("alignment").GetInt32()); Assert.Equal(120, character.GetProperty("line_spacing").GetDouble());
        await c.Draw(doc, "corel_edit_text", new { static_ids = new[] { id }, action = "set_text", text = "Один\r\nДва" });
        Assert.Equal("Один\rДва", (await c.Inspect(doc)).GetProperty("shapes")[0].GetProperty("text").GetString());
        var saved = (await c.Inspect(doc)).GetRawText();
        var invalid = await c.DrawFailure(doc, "corel_edit_text", new { static_ids = new[] { id }, action = "set_text", text = "🦔" });
        Assert.Equal("TEXT_UNSUPPORTED_CHARACTER", invalid.Error!.Code); Assert.False(invalid.Error.Applied); Assert.Equal(saved, (await c.Inspect(doc)).GetRawText());
    }
    [LiveCorelFact]
    public async Task PreflightReportsUnknownAndNeverFixesUntilExplicitConvertAndStyle()
    {
        using var c = new DrawingClient(); var doc = await c.Create(); var layer = Layer(await c.Inspect(doc));
        var created = await c.Draw(doc, "corel_create_shapes", new { layer_id = layer, items = new[] { new { kind = "rectangle", x_mm = 10, y_mm = 10, width_mm = 30, height_mm = 20 } } });
        int id = created.GetProperty("created_static_ids")[0].GetInt32();
        object[] checks = [new { kind = "open_contours" }, new { kind = "dimensions", min_width_mm = 29.9, max_width_mm = 30.1, min_height_mm = 19.9, max_height_mm = 20.1, tolerance_mm = 0.001 }, new { kind = "text_present" }, new { kind = "outline_width", expected_mm = 0.2, tolerance_mm = 0.001 }, new { kind = "outline_color", color = new { model = "cmyk", c = 0, m = 0, y = 0, k = 100 }, tolerance = 0 }];
        var before = (await c.Inspect(doc)).GetRawText();
        var initial = await c.Ok("corel_preflight", new { document_id = doc, page_index = 1, static_ids = new[] { id }, checks });
        Assert.Equal("unknown", initial.GetProperty("objects")[0].GetProperty("checks")[0].GetProperty("status").GetString());
        Assert.Equal(before, (await c.Inspect(doc)).GetRawText());
        await c.Draw(doc, "corel_convert_to_curves", new { static_ids = new[] { id } });
        await c.Draw(doc, "corel_style_shapes", new { static_ids = new[] { id }, fill = new { kind = "none" }, outline = new { kind = "uniform", width_mm = 0.2, color = new { model = "cmyk", c = 0, m = 0, y = 0, k = 100 } } });
        var final = await c.Ok("corel_preflight", new { document_id = doc, page_index = 1, static_ids = new[] { id }, checks });
        Assert.All(final.GetProperty("objects")[0].GetProperty("checks").EnumerateArray(), check => Assert.Equal("passed", check.GetProperty("status").GetString()));
    }
    private static object P(double x, double y) => new { x_mm = x, y_mm = y };
    private static string Layer(JsonElement inspection) => inspection.GetProperty("layers").EnumerateArray().First(layer => !layer.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    private static void Point(JsonElement point, double x, double y) { Assert.InRange(point.GetProperty("x_mm").GetDouble(), x - 0.0001, x + 0.0001); Assert.InRange(point.GetProperty("y_mm").GetDouble(), y - 0.0001, y + 0.0001); }
}
