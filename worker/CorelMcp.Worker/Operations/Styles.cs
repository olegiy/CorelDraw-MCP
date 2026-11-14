namespace CorelMcp.Worker.Operations;

internal static class Styles
{
    public static Action Prepare(DrawingTarget target, StyleCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds);
        var fillColor = args.Fill is UniformFill f ? Color(c, f.Color) : null;
        var outlineColor = args.Outline is UniformOutline o ? Color(c, o.Color) : null;
        return () =>
        {
            foreach (var shape in shapes)
            {
                var ids = target.TreeIds(shape);
                if (args.Fill is not null)
                {
                    var fill = c.Track(shape.Fill); c.Mutating();
                    if (args.Fill is NoFill) fill.ApplyNoFill(); else fill.ApplyUniformFill(fillColor!);
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
    private static VGCore.Color Color(OperationContext c, ColorSpec color) => c.Track(color switch
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
}
