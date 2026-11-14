namespace CorelMcp.Worker.Operations;

internal static class Preflight
{
    public static object Execute(OperationContext c, PreflightArguments args)
    {
        c.Stage("preflight_checks"); var target = new DrawingTarget(c, args.PageIndex);
        var shapes = ShapeTraversal.Resolve(c, target.Page, args.StaticIds); List<object> objects = [];
        var mm = c.App.ConvertUnits(1, target.Document.Unit, VGCore.cdrUnit.cdrMillimeter);
        foreach (var shape in shapes)
        {
            List<object> checks = [];
            VGCore.Shape[] leaves = []; string? unsupported = null;
            try
            {
                var all = new[] { shape }.Concat(shape.Type == VGCore.cdrShapeType.cdrGroupShape ? ShapeTraversal.Walk(c, c.Track(shape.Shapes)).Select(item => item.Shape) : []).ToArray();
                foreach (var item in all)
                {
                    if ((int)item.Type is not (1 or 2 or 3 or 4 or 6 or 7) || c.Track(item.Effects).Count > 0) unsupported = "Неподдерживаемый тип или live effects.";
                    var clip = item.PowerClip; if (clip is not null) { c.Track(clip); unsupported = "PowerClip требует отдельной проверки."; }
                }
                leaves = all.Where(item => item.Type != VGCore.cdrShapeType.cdrGroupShape).ToArray();
                if (leaves.Length == 0) unsupported = "Пустая группа не проверена.";
            }
            catch (OperationFailure error) { unsupported = error.Code; }
            foreach (var rule in args.Checks)
            {
                string name = rule switch { OpenContoursCheck => "open_contours", TextPresentCheck => "text_present", DimensionsCheck => "dimensions", OutlineWidthCheck => "outline_width", _ => "outline_color" };
                object Result(string status, string? reason = null, object? actual = null) => Dto.Of(("kind", name), ("status", status), ("reason", reason), ("actual", actual));
                if (unsupported is not null) { checks.Add(Result("unknown", unsupported)); continue; }
                try
                {
                    switch (rule)
                    {
                        case TextPresentCheck:
                            int texts = leaves.Count(item => item.Type == VGCore.cdrShapeType.cdrTextShape);
                            checks.Add(Result(texts == 0 ? "passed" : "failed", texts == 0 ? null : "Сохранились текстовые фигуры.", texts)); break;
                        case DimensionsCheck d:
                            double width = shape.SizeWidth * mm, height = shape.SizeHeight * mm;
                            bool valid = double.IsFinite(width) && double.IsFinite(height);
                            bool fits = width >= d.MinWidthMm - d.ToleranceMm && width <= d.MaxWidthMm + d.ToleranceMm && height >= d.MinHeightMm - d.ToleranceMm && height <= d.MaxHeightMm + d.ToleranceMm;
                            checks.Add(Result(!valid ? "unknown" : fits ? "passed" : "failed", actual: valid ? Dto.Of(("width_mm", width), ("height_mm", height)) : null)); break;
                        case OpenContoursCheck:
                            if (leaves.Any(item => item.Type != VGCore.cdrShapeType.cdrCurveShape)) { checks.Add(Result("unknown", "Для проверки нужны явные кривые; автоматического преобразования нет.")); break; }
                            var data = leaves.Select(item => Curves.Read(target, item)).ToArray(); int open = data.Sum(item => item.Paths.Count(path => !path.Closed));
                            checks.Add(Result(open == 0 ? "passed" : "failed", actual: open)); break;
                        case OutlineWidthCheck w:
                            bool widths = leaves.All(item => { var outline = c.Track(item.Outline); return (int)outline.Type != 0 && Math.Abs(outline.Width * mm - w.ExpectedMm) <= w.ToleranceMm; });
                            checks.Add(Result(widths ? "passed" : "failed", widths ? null : "Обводка отсутствует или ширина вне заданного допуска.")); break;
                        case OutlineColorCheck color:
                            string status = "passed";
                            foreach (var item in leaves)
                            {
                                var outline = c.Track(item.Outline); if ((int)outline.Type == 0) { status = "failed"; break; }
                                var actual = c.Track(outline.Color);
                                int[] expected, observed;
                                if (color.Color is RgbSpec rgb && actual.Type == VGCore.cdrColorType.cdrColorRGB) { expected = [rgb.R, rgb.G, rgb.B]; observed = [actual.RGBRed, actual.RGBGreen, actual.RGBBlue]; }
                                else if (color.Color is CmykSpec cmyk && actual.Type == VGCore.cdrColorType.cdrColorCMYK) { expected = [cmyk.C, cmyk.M, cmyk.Y, cmyk.K]; observed = [actual.CMYKCyan, actual.CMYKMagenta, actual.CMYKYellow, actual.CMYKBlack]; }
                                else { status = "unknown"; continue; }
                                if (expected.Zip(observed).Any(pair => Math.Abs(pair.First - pair.Second) > color.Tolerance)) { status = "failed"; break; }
                            }
                            checks.Add(Result(status, status == "unknown" ? "Модели цвета различаются; ICC-конверсия не выполнялась." : null)); break;
                    }
                }
                catch (Exception error) { checks.Add(Result("unknown", error is OperationFailure failure ? failure.Code : "CHECK_UNAVAILABLE")); }
            }
            objects.Add(Dto.Of(("static_id", shape.StaticID), ("checks", checks)));
        }
        return Dto.Of(("objects", objects), ("automatic_fixes", false), ("machine_compatibility_guaranteed", false));
    }
}
