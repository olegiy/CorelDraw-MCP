namespace CorelMcp.Worker.Operations;

internal static class Styles
{
    public static Action Prepare(DrawingTarget target, StyleCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds);
        var fillColor = args.Fill is UniformFill f ? Color(c, f.Color) : null;
        var outlineColor = args.Outline is UniformOutline o ? Color(c, o.Color) : null;
        var fountain = args.Fill is FountainFill gradient ? PrepareFountain(c, gradient) : null;
        return () =>
        {
            foreach (var shape in shapes)
            {
                var ids = target.TreeIds(shape);
                if (args.Fill is not null)
                {
                    var fill = c.Track(shape.Fill); c.Mutating();
                    if (args.Fill is NoFill) fill.ApplyNoFill();
                    else if (args.Fill is FountainFill) ApplyFountain(c, fill, fountain!);
                    else fill.ApplyUniformFill(fillColor!);
                    target.Observe(ids, shape);
                }
                if (args.Outline is not null)
                {
                    var outline = c.Track(shape.Outline); c.Mutating();
                    if (args.Outline is NoOutline) outline.SetNoOutline();
                    else outline.SetProperties(Width: target.U(((UniformOutline)args.Outline).WidthMm), Color: outlineColor!);
                    target.Observe(ids, shape);
                }
            }
        };
    }
    // Every stop color (and each mapped enum) is resolved before the mutation closure, like UniformFill above.
    private static PreparedFountain PrepareFountain(OperationContext c, FountainFill args) => new(
        DrawingContracts.FountainType(args.Type),
        args.AngleDeg ?? 0, args.Steps ?? 256, args.EdgePad ?? 0, args.MidPoint ?? 50,
        args.BlendType is null ? VGCore.cdrFountainFillBlendType.cdrDirectFountainFillBlend : DrawingContracts.FountainBlendType(args.BlendType),
        args.CenterOffsetXPercent ?? 0, args.CenterOffsetYPercent ?? 0,
        args.Spread is null ? null : DrawingContracts.FountainSpread(args.Spread), args.SmoothBlend,
        Color(c, args.Stops[0].Color), Color(c, args.Stops[^1].Color),
        args.Stops[1..^1].Select(stop => (Color: Color(c, stop.Color), stop.Position)).ToArray());
    private static void ApplyFountain(OperationContext c, VGCore.Fill fill, PreparedFountain fountain)
    {
        var applied = c.Track(fill.ApplyFountainFill(fountain.First, fountain.Last, fountain.Type, fountain.Angle,
            fountain.Steps, fountain.EdgePad, fountain.MidPoint, fountain.Blend,
            // CenterOffsetX/Y are percentages of the object width/height in API 27 — passed as-is, never converted with target.U().
            fountain.CenterXPercent, fountain.CenterYPercent));
        if (fountain.Middle.Length > 0)
        {
            var colors = c.Track(applied.Colors);
            foreach (var (color, position) in fountain.Middle) colors.Add(color, position);
        }
        if (fountain.Spread is { } spread) applied.SpreadMethod = spread;
        if (fountain.SmoothBlend is { } smooth) applied.SmoothBlend = smooth;
    }
    // Reused by Operations/DropShadow for the preflight shadow color (same "colors before the mutation closure" rule).
    internal static VGCore.Color Color(OperationContext c, ColorSpec color) => c.Track(color switch
    {
        RgbSpec rgb => c.App.CreateRGBColor(rgb.R, rgb.G, rgb.B),
        CmykSpec cmyk => c.App.CreateCMYKColor(cmyk.C, cmyk.M, cmyk.Y, cmyk.K),
        _ => throw new OperationFailure("INVALID_INPUT", "Неподдерживаемая модель цвета.")
    });
    public static object ColorInfo(OperationContext c, VGCore.Color color)
    {
        var type = color.Type;
        if (type == VGCore.cdrColorType.cdrColorRGB) return Dto.Of(("model", "rgb"), ("r", color.RGBRed), ("g", color.RGBGreen), ("b", color.RGBBlue));
        if (type == VGCore.cdrColorType.cdrColorCMYK) return Dto.Of(("model", "cmyk"), ("c", color.CMYKCyan), ("m", color.CMYKMagenta), ("y", color.CMYKYellow), ("k", color.CMYKBlack));
        return Dto.Of(("native_type", (int)type));
    }
    public static object FountainInfo(OperationContext c, VGCore.FountainFill fountain)
    {
        var colors = c.Track(fountain.Colors);
        // Live-verified: IVGFountainColors.Item is 0-based (unlike IVGShapes), valid indices 0..Count-1.
        // Count is cached once so the loop condition does not re-read the COM property on every iteration.
        var count = colors.Count;
        var stops = new List<object>(count);
        for (int index = 0; index < count; index++)
        {
            var stop = c.Track(colors[index]);
            stops.Add(Dto.Of(("position", stop.Position), ("color", ColorInfo(c, c.Track(stop.Color)))));
        }
        return Dto.Of(("type", TypeName(fountain.Type)), ("angle_deg", fountain.Angle),
            ("center_offset_x_percent", fountain.CenterOffsetX), ("center_offset_y_percent", fountain.CenterOffsetY),
            ("spread", SpreadName(fountain.SpreadMethod)), ("steps", fountain.Steps), ("edge_pad", fountain.EdgePad),
            ("mid_point", fountain.MidPoint), ("smooth_blend", fountain.SmoothBlend), ("blend_type", BlendName(fountain.BlendType)),
            ("stops", stops.ToArray()));
    }
    // Read-back names mirror the request enum names; an unmapped native value falls back to its raw int instead of a silent rename.
    private static string TypeName(VGCore.cdrFountainFillType type) => type switch
    {
        VGCore.cdrFountainFillType.cdrLinearFountainFill => "linear",
        VGCore.cdrFountainFillType.cdrRadialFountainFill => "radial",
        VGCore.cdrFountainFillType.cdrConicalFountainFill => "conical",
        VGCore.cdrFountainFillType.cdrSquareFountainFill => "square",
        _ => ((int)type).ToString()
    };
    private static string SpreadName(VGCore.cdrFountainFillSpreadMethod spread) => spread switch
    {
        VGCore.cdrFountainFillSpreadMethod.cdrFountainFillSpreadMethodPad => "pad",
        VGCore.cdrFountainFillSpreadMethod.cdrFountainFillSpreadMethodReflect => "reflect",
        VGCore.cdrFountainFillSpreadMethod.cdrFountainFillSpreadMethodRepeat => "repeat",
        _ => ((int)spread).ToString()
    };
    private static string BlendName(VGCore.cdrFountainFillBlendType blend) => blend switch
    {
        VGCore.cdrFountainFillBlendType.cdrDirectFountainFillBlend => "direct",
        VGCore.cdrFountainFillBlendType.cdrRainbowCWFountainFillBlend => "rainbow_cw",
        VGCore.cdrFountainFillBlendType.cdrRainbowCCWFountainFillBlend => "rainbow_ccw",
        VGCore.cdrFountainFillBlendType.cdrCustomFountainFillBlend => "custom",
        _ => ((int)blend).ToString()
    };
}

// Resolved COM inputs for one fountain fill request: colors are created once in preflight and reused for every target.
// CenterXPercent/CenterYPercent are object-relative percent offsets (API 27 semantics): they go to COM verbatim,
// without any target.U() unit conversion.
internal sealed record PreparedFountain(VGCore.cdrFountainFillType Type, double Angle, int Steps, int EdgePad, int MidPoint,
    VGCore.cdrFountainFillBlendType Blend, double CenterXPercent, double CenterYPercent, VGCore.cdrFountainFillSpreadMethod? Spread, bool? SmoothBlend,
    VGCore.Color First, VGCore.Color Last, (VGCore.Color Color, int Position)[] Middle);
