using System.Reflection;
using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class LayoutTests
{
    private static object Box(int id, double w, double h, double x = 0, double y = 0, string revision = "geometry")
        => new { static_id = id, left_mm = x, bottom_mm = y, width_mm = w, height_mm = h, geometry_revision = revision };
    private static object Input(object[] boxes, double margin = 5) => new { page_width_mm = 100, page_height_mm = 80, margin_left_mm = margin, margin_right_mm = margin,
        margin_top_mm = margin, margin_bottom_mm = margin, gap_x_mm = 2, gap_y_mm = 2, boxes };
    [Fact]
    public void ShelfUsesInputOrderMarginsAndGapsAndReportsInvalidOrOversizedItems()
    {
        var result = Run("Shelf", "LayoutInput", Input([Box(1,40,20),Box(2,40,20),Box(3,40,20),Box(4,200,20),Box(5,0,20)]));
        var placements = result.GetProperty("placements"); Assert.Equal(3, placements.GetArrayLength());
        Position(placements[0], 1, 5, 5); Position(placements[1], 2, 47, 5); Position(placements[2], 3, 5, 27);
        Assert.Equal(new[] { 4, 5 }, result.GetProperty("unplaced").EnumerateArray().Select(item => item.GetProperty("static_id").GetInt32()));
    }
    [Fact]
    public void ShelfAcceptsEmptyInputAndExactPageBoundary()
    {
        Assert.Empty(Run("Shelf", "LayoutInput", Input([])).GetProperty("placements").EnumerateArray());
        var result = Run("Shelf", "LayoutInput", Input([Box(1,90,70)])); Assert.Empty(result.GetProperty("unplaced").EnumerateArray()); Position(result.GetProperty("placements")[0], 1, 5, 5);
        var overflow = Run("Shelf", "LayoutInput", Input([Box(1,90,70.001)])); Assert.Single(overflow.GetProperty("unplaced").EnumerateArray());
        Assert.Throws<OperationFailure>(() => Run("Shelf", "LayoutInput", Input([Box(1,1,1)], -1)));
    }
    [Fact]
    public void EqualGapsSortsSpatiallyAndUsesActualWidths()
    {
        var result = Run("Align", "AlignInput", new { axis = "x", mode = "equal_gaps", boxes = new[] { Box(3,10,5,60), Box(1,10,5), Box(2,20,5,20) }, reference = new { left_mm = 0, bottom_mm = 0, width_mm = 100, height_mm = 80 } });
        var values = result.EnumerateArray().ToArray(); Position(values[0], 1, 0, 0); Position(values[1], 2, 40, 0); Position(values[2], 3, 90, 0);
    }
    [Fact]
    public void AlignmentUsesGeometryCenterAndLeavesOtherAxisIntact()
    {
        var result = Run("Align", "AlignInput", new { axis = "x", mode = "center", boxes = new[] { Box(1,10,5,10,3), Box(2,20,7,60,4) }, reference = new { left_mm = 0, bottom_mm = 0, width_mm = 100, height_mm = 80 } });
        Position(result[0], 1, 45, 3); Position(result[1], 2, 40, 4);
    }
    [Fact]
    public void GridIncludesOriginalCellAndCountsAllShapesAgainstCopyLimit()
    {
        var result = Run("Grid", "GridInput", new { cell = new { left_mm = 10, bottom_mm = 20, width_mm = 30, height_mm = 10 }, rows = 2, columns = 3,
            gap_x_mm = 5, gap_y_mm = 2, anchor_x_mm = 0, anchor_y_mm = 0, shapes_per_cell = 3 });
        var offsets = result.EnumerateArray().ToArray(); Assert.Equal(6, offsets.Length);
        Assert.Equal(-10, offsets[0].GetProperty("x_mm").GetDouble()); Assert.Equal(-20, offsets[0].GetProperty("y_mm").GetDouble());
        Assert.Equal(25, offsets[1].GetProperty("x_mm").GetDouble()); Assert.Equal(-8, offsets[3].GetProperty("y_mm").GetDouble());
        Assert.Throws<OperationFailure>(() => Run("Grid", "GridInput", new { cell = new { left_mm = 0, bottom_mm = 0, width_mm = 1, height_mm = 1 }, rows = 1, columns = 1000,
            gap_x_mm = 0, gap_y_mm = 0, anchor_x_mm = 0, anchor_y_mm = 0, shapes_per_cell = 2 }));
    }
    [Fact]
    public void RevisionChangesForGeometryEvenWhenBoundsStayTheSame()
    {
        var first = Run("Shelf", "LayoutInput", Input([Box(1,10,10,revision:"before")]));
        var second = Run("Shelf", "LayoutInput", Input([Box(1,10,10,revision:"after")]));
        Assert.NotEqual(first.GetProperty("layout_revision").GetString(), second.GetProperty("layout_revision").GetString());
        Assert.Equal(first.GetRawText(), Run("Shelf", "LayoutInput", Input([Box(1,10,10,revision:"before")])).GetRawText());
    }
    private static JsonElement Run(string methodName, string inputName, object args)
    {
        var assembly = typeof(OperationDispatcher).Assembly;
        var planner = assembly.GetType("CorelMcp.Worker.Operations.LayoutPlanner"); Assert.NotNull(planner);
        var input = assembly.GetType("CorelMcp.Worker.Operations." + inputName); Assert.NotNull(input);
        var method = planner.GetMethod(methodName); Assert.NotNull(method);
        try { return JsonSerializer.SerializeToElement(method.Invoke(null, [JsonSerializer.Deserialize(JsonSerializer.Serialize(args), input, Protocol.JsonOptions)]), Protocol.JsonOptions); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
    private static void Position(JsonElement value, int id, double x, double y)
    {
        Assert.Equal(id,value.GetProperty("static_id").GetInt32()); Assert.Equal(x,value.GetProperty("x_mm").GetDouble(),8); Assert.Equal(y,value.GetProperty("y_mm").GetDouble(),8);
    }
}
