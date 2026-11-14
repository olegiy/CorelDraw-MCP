using System.Security.Cryptography;
using System.Text.Json;

namespace CorelMcp.Worker.Operations;

internal sealed record BoundsMm(double LeftMm, double BottomMm, double WidthMm, double HeightMm);
internal sealed record LayoutBox(int StaticId, double LeftMm, double BottomMm, double WidthMm, double HeightMm, string GeometryRevision);
internal sealed record LayoutInput(double PageWidthMm, double PageHeightMm, double MarginLeftMm, double MarginRightMm, double MarginTopMm, double MarginBottomMm,
    double GapXMm, double GapYMm, LayoutBox[] Boxes);
internal sealed record AlignInput(string Axis, string Mode, LayoutBox[] Boxes, BoundsMm Reference);
internal sealed record GridInput(BoundsMm Cell, int Rows, int Columns, double GapXMm, double GapYMm, double AnchorXMm, double AnchorYMm, int ShapesPerCell);
internal sealed record Placement(int StaticId, double XMm, double YMm, double WidthMm, double HeightMm);
internal sealed record Unplaced(int StaticId, string Reason);
internal sealed record LayoutPlan(Placement[] Placements, Unplaced[] Unplaced, string LayoutRevision);

internal static class LayoutPlanner
{
    private static void Check(bool value, string code = "INVALID_INPUT") { if (!value) throw new OperationFailure(code, "Параметры или размеры не поддерживают запрошенную раскладку."); }
    private static void Finite(params double[] values) => Check(values.All(double.IsFinite));
    private static void Boxes(LayoutBox[] boxes)
    {
        Check(boxes.Length <= 100 && boxes.Select(box => box.StaticId).Distinct().Count() == boxes.Length);
        foreach (var box in boxes) Finite(box.LeftMm, box.BottomMm, box.WidthMm, box.HeightMm);
    }
    public static LayoutPlan Shelf(LayoutInput input)
    {
        Boxes(input.Boxes); Finite(input.PageWidthMm, input.PageHeightMm, input.MarginLeftMm, input.MarginRightMm, input.MarginTopMm, input.MarginBottomMm, input.GapXMm, input.GapYMm);
        Check(input.PageWidthMm > 0 && input.PageHeightMm > 0 && input.MarginLeftMm >= 0 && input.MarginRightMm >= 0 && input.MarginTopMm >= 0 && input.MarginBottomMm >= 0 && input.GapXMm >= 0 && input.GapYMm >= 0);
        Check(input.MarginLeftMm + input.MarginRightMm <= input.PageWidthMm && input.MarginTopMm + input.MarginBottomMm <= input.PageHeightMm);
        double right = input.PageWidthMm - input.MarginRightMm, top = input.PageHeightMm - input.MarginTopMm;
        double x = input.MarginLeftMm, y = input.MarginBottomMm, rowHeight = 0;
        List<Placement> placements = []; List<Unplaced> unplaced = [];
        foreach (var box in input.Boxes)
        {
            if (box.WidthMm <= 0 || box.HeightMm <= 0) { unplaced.Add(new(box.StaticId, "ZERO_SIZE_BOUNDS")); continue; }
            if (box.WidthMm > right - input.MarginLeftMm || box.HeightMm > top - input.MarginBottomMm) { unplaced.Add(new(box.StaticId, "TOO_LARGE")); continue; }
            if (x + box.WidthMm > right) { x = input.MarginLeftMm; y += rowHeight + input.GapYMm; rowHeight = 0; }
            if (y + box.HeightMm > top) { unplaced.Add(new(box.StaticId, "PAGE_FULL")); continue; }
            placements.Add(new(box.StaticId, x, y, box.WidthMm, box.HeightMm));
            x += box.WidthMm + input.GapXMm; rowHeight = Math.Max(rowHeight, box.HeightMm);
        }
        return new(placements.ToArray(), unplaced.ToArray(), Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, Protocol.JsonOptions))));
    }
    public static Placement[] Align(AlignInput input)
    {
        Boxes(input.Boxes); var r = input.Reference;
        Finite(r.LeftMm, r.BottomMm, r.WidthMm, r.HeightMm); Check(r.WidthMm >= 0 && r.HeightMm >= 0);
        Check(input.Axis is "x" or "y" && input.Mode is "min" or "center" or "max" or "equal_gaps");
        bool x = input.Axis == "x"; double start = x ? r.LeftMm : r.BottomMm, extent = x ? r.WidthMm : r.HeightMm;
        double Size(LayoutBox b) => x ? b.WidthMm : b.HeightMm;
        double Position(LayoutBox b) => x ? b.LeftMm : b.BottomMm;
        Placement At(LayoutBox b, double position) => new(b.StaticId, x ? position : b.LeftMm, x ? b.BottomMm : position, b.WidthMm, b.HeightMm);
        if (input.Mode == "equal_gaps")
        {
            Check(input.Boxes.Length >= 2);
            var ordered = input.Boxes.OrderBy(Position).ThenBy(box => box.StaticId).ToArray();
            double gap = (extent - ordered.Sum(Size)) / (ordered.Length - 1); Check(gap >= 0, "INSUFFICIENT_SPACE");
            List<Placement> result = []; double position = start;
            foreach (var box in ordered) { result.Add(At(box, position)); position += Size(box) + gap; }
            return result.ToArray();
        }
        return input.Boxes.Select(box => At(box, input.Mode switch { "min" => start, "center" => start + (extent - Size(box)) / 2, _ => start + extent - Size(box) })).ToArray();
    }
    public static PointMm[] Grid(GridInput input)
    {
        var c = input.Cell; Finite(c.LeftMm, c.BottomMm, c.WidthMm, c.HeightMm, input.GapXMm, input.GapYMm, input.AnchorXMm, input.AnchorYMm);
        Check(c.WidthMm > 0 && c.HeightMm > 0, "ZERO_SIZE_BOUNDS"); Check(input.Rows is >= 1 and <= 1000 && input.Columns is >= 1 and <= 1000 && input.ShapesPerCell > 0 && input.GapXMm >= 0 && input.GapYMm >= 0);
        Check(((long)input.Rows * input.Columns - 1) * input.ShapesPerCell <= 1000, "RESULT_LIMIT_EXCEEDED");
        List<PointMm> offsets = [];
        for (int row = 0; row < input.Rows; row++) for (int column = 0; column < input.Columns; column++)
            offsets.Add(new(input.AnchorXMm - c.LeftMm + column * (c.WidthMm + input.GapXMm), input.AnchorYMm - c.BottomMm + row * (c.HeightMm + input.GapYMm)));
        return offsets.ToArray();
    }
}
