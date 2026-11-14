using System.Text.Json;
using System.Text.Json.Serialization;
using CorelMcp.Worker.Validation;

namespace CorelMcp.Worker.Operations;

internal interface IDrawingCommand;
internal sealed record DrawingArguments(string RequestId, string DocumentId, int PageIndex, IDrawingCommand Command, int DeadlineSeconds = 600)
    : IRequestArguments, IDocumentArguments, IDeadlineArguments;
internal sealed record DrawingBinding(string RequestId, string DocumentId, int PageIndex, int DeadlineSeconds = 600);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectangleItem), "rectangle"), JsonDerivedType(typeof(EllipseItem), "ellipse"), JsonDerivedType(typeof(LineItem), "line")]
internal abstract record ShapeItem;
internal sealed record RectangleItem(double XMm, double YMm, double WidthMm, double HeightMm) : ShapeItem;
internal sealed record EllipseItem(double XMm, double YMm, double WidthMm, double HeightMm) : ShapeItem;
internal sealed record LineItem(double X1Mm, double Y1Mm, double X2Mm, double Y2Mm) : ShapeItem;
internal sealed record CreateShapesCommand(string LayerId, ShapeItem[] Items) : IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(MoveCommand), "move"), JsonDerivedType(typeof(ScaleCommand), "scale"), JsonDerivedType(typeof(RotateCommand), "rotate"), JsonDerivedType(typeof(FlipCommand), "flip")]
internal abstract record TransformCommand(int[] StaticIds) : IDrawingCommand;
internal sealed record MoveCommand(int[] StaticIds, double DxMm, double DyMm) : TransformCommand(StaticIds);
internal sealed record ScaleCommand(int[] StaticIds, double Sx, double Sy, double AnchorXMm, double AnchorYMm) : TransformCommand(StaticIds);
internal sealed record RotateCommand(int[] StaticIds, double AngleDeg, double AnchorXMm, double AnchorYMm) : TransformCommand(StaticIds);
internal sealed record FlipCommand(int[] StaticIds, string Axis, double AnchorXMm, double AnchorYMm) : TransformCommand(StaticIds);
internal sealed record DuplicateCommand(int[] StaticIds, int Copies, double StepXMm, double StepYMm) : IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "model")]
[JsonDerivedType(typeof(RgbSpec), "rgb"), JsonDerivedType(typeof(CmykSpec), "cmyk")]
internal abstract record ColorSpec;
internal sealed record RgbSpec(int R, int G, int B) : ColorSpec;
internal sealed record CmykSpec(int C, int M, int Y, int K) : ColorSpec;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoFill), "none"), JsonDerivedType(typeof(UniformFill), "uniform")]
internal abstract record FillSpec;
internal sealed record NoFill : FillSpec;
internal sealed record UniformFill(ColorSpec Color) : FillSpec;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoOutline), "none"), JsonDerivedType(typeof(UniformOutline), "uniform")]
internal abstract record OutlineSpec;
internal sealed record NoOutline : OutlineSpec;
internal sealed record UniformOutline(ColorSpec Color, double WidthMm) : OutlineSpec;
internal sealed record StyleCommand(int[] StaticIds, FillSpec? Fill = null, OutlineSpec? Outline = null) : IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(CreateLayerCommand), "create"), JsonDerivedType(typeof(RenameLayerCommand), "rename"), JsonDerivedType(typeof(LayerFlagsCommand), "set_flags"), JsonDerivedType(typeof(MoveToLayerCommand), "move_shapes")]
internal abstract record LayerCommand : IDrawingCommand;
internal sealed record CreateLayerCommand(string PageId, string Name) : LayerCommand;
internal sealed record RenameLayerCommand(string LayerId, string Name) : LayerCommand;
internal sealed record LayerFlagsCommand(string LayerId, bool? Visible = null, bool? Editable = null, bool? Printable = null) : LayerCommand;
internal sealed record MoveToLayerCommand(string LayerId, int[] StaticIds) : LayerCommand;
internal sealed record GroupCommand(string Action, int[] StaticIds) : IDrawingCommand;
internal sealed record BooleanCommand(string Action, int LeftId, int RightId, bool KeepLeft, bool KeepRight) : IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ArtisticTextCommand), "artistic"), JsonDerivedType(typeof(ParagraphTextCommand), "paragraph")]
internal abstract record CreateTextCommand(string LayerId, string Text, string FontFamily, double FontSizePt) : IDrawingCommand;
internal sealed record ArtisticTextCommand(string LayerId, string Text, string FontFamily, double FontSizePt, double XMm, double YMm)
    : CreateTextCommand(LayerId, Text, FontFamily, FontSizePt);
internal sealed record ParagraphTextCommand(string LayerId, string Text, string FontFamily, double FontSizePt, double XMm, double YMm, double WidthMm, double HeightMm)
    : CreateTextCommand(LayerId, Text, FontFamily, FontSizePt);
internal sealed record ConvertCommand(int[] StaticIds) : IDrawingCommand;
internal sealed record AlignCommand(int[] StaticIds, string Axis, string Action, string Reference, string? Align = null, string? Distribution = null, int? ReferenceId = null) : IDrawingCommand;
internal sealed record GridCommand(int[] StaticIds, int Rows, int Columns, double AnchorXMm, double AnchorYMm, double GapXMm = 0, double GapYMm = 0) : IDrawingCommand;
internal sealed record LayoutCommand(int[] StaticIds, bool DryRun = true, string? ExpectedRevision = null, double MarginLeftMm = 0, double MarginRightMm = 0,
    double MarginTopMm = 0, double MarginBottomMm = 0, double GapXMm = 0, double GapYMm = 0) : IDrawingCommand;

internal static class DrawingContracts
{
    private static readonly HashSet<string> BindingNames = ["request_id", "document_id", "page_index", "deadline_seconds"];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowOutOfOrderMetadataProperties = true
    };
    public static bool IsDrawingTool(string tool) => tool is "corel_create_shapes" or "corel_transform_shapes" or "corel_duplicate_shapes" or "corel_style_shapes"
        or "corel_manage_layers" or "corel_group_shapes" or "corel_boolean_shapes" or "corel_create_text" or "corel_convert_to_curves"
        or "corel_align_shapes" or "corel_grid_duplicate" or "corel_layout_shapes" or "corel_import" or "corel_manage_pages" or "corel_powerclip";
    public static DrawingArguments Parse(string tool, JsonElement args)
    {
        try
        {
            RejectNull(args);
            var fields = args.EnumerateObject().ToArray();
            var binding = Read<DrawingBinding>(JsonSerializer.SerializeToElement(fields.Where(p => BindingNames.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value)));
            OperationContracts.Require(binding.RequestId, 200, "request_id"); OperationContracts.Require(binding.DocumentId, 100, "document_id");
            OperationContracts.Page(binding.PageIndex); OperationContracts.Range(binding.DeadlineSeconds, 1, 3600, "deadline_seconds");
            var payload = JsonSerializer.SerializeToElement(fields.Where(p => !BindingNames.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value));
            IDrawingCommand command = tool switch
            {
                "corel_create_shapes" => Read<CreateShapesCommand>(payload), "corel_transform_shapes" => Read<TransformCommand>(payload),
                "corel_duplicate_shapes" => Read<DuplicateCommand>(payload), "corel_style_shapes" => Read<StyleCommand>(payload),
                "corel_manage_layers" => Read<LayerCommand>(payload), "corel_group_shapes" => Read<GroupCommand>(payload),
                "corel_boolean_shapes" => Read<BooleanCommand>(payload), "corel_create_text" => Read<CreateTextCommand>(payload),
                "corel_convert_to_curves" => Read<ConvertCommand>(payload), "corel_align_shapes" => Read<AlignCommand>(payload),
                "corel_grid_duplicate" => Read<GridCommand>(payload), "corel_layout_shapes" => Read<LayoutCommand>(payload),
                "corel_import" => Read<ImportCommand>(payload),"corel_manage_pages"=>Read<PageCommand>(payload),"corel_powerclip"=>Read<ClipCommand>(payload),_ => throw new JsonException()
            };
            Validate(command);
            return new(binding.RequestId, binding.DocumentId, binding.PageIndex, command, binding.DeadlineSeconds);
        }
        catch (JsonException) { throw new OperationFailure("INVALID_INPUT", "Параметры не соответствуют строгой схеме рисования."); }
        catch (TextValidationException error) { throw new OperationFailure(error.Code, error.Message); }
    }
    internal static T Read<T>(JsonElement element) => element.Deserialize<T>(Options) ?? throw new JsonException();
    internal static void RejectNull(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) throw new JsonException();
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) RejectNull(property.Value);
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectNull(item);
    }
    internal static void Check(bool condition) { if (!condition) throw new OperationFailure("INVALID_INPUT", "Некорректные параметры рисования."); }
    internal static void Coord(params double[] values) => Check(values.All(value => double.IsFinite(value) && Math.Abs(value) <= 1000000));
    internal static void Id(string value) => OperationContracts.Require(value, 100, "opaque ID");
    internal static void Ids(int[] values) => OperationContracts.StaticIds(values);
    internal static void Name(string value) { OperationContracts.Require(value, 200, "name/font_family"); CorelText.Validate(value); }
    internal static void Color(ColorSpec value)
    {
        switch (value)
        {
            case RgbSpec rgb: foreach (var n in new[] { rgb.R, rgb.G, rgb.B }) OperationContracts.Range(n, 0, 255, "RGB"); break;
            case CmykSpec cmyk: foreach (var n in new[] { cmyk.C, cmyk.M, cmyk.Y, cmyk.K }) OperationContracts.Range(n, 0, 100, "CMYK"); break;
        }
    }
    private static void Validate(IDrawingCommand value)
    {
        if(value is ImportCommand or PageCommand or ClipCommand){AssemblyContracts.Validate(value);return;}
        switch (value)
        {
            case AlignCommand align:
                Ids(align.StaticIds); Check(align.Axis is "x" or "y"); Check(align.Reference is "page" or "selection" or "shape");
                Check(align.Reference == "shape" ? align.ReferenceId is > 0 : align.ReferenceId is null);
                Check(align.Action == "align" ? align.Align is "min" or "center" or "max" && align.Distribution is null
                    : align.Action == "distribute" && align.Distribution == "equal_gaps" && align.Align is null && align.StaticIds.Length >= 2); break;
            case GridCommand grid:
                Ids(grid.StaticIds); OperationContracts.Range(grid.Rows,1,1000,"rows"); OperationContracts.Range(grid.Columns,1,1000,"columns");
                Check(((long)grid.Rows * grid.Columns - 1) * grid.StaticIds.Length <= 1000); Coord(grid.AnchorXMm,grid.AnchorYMm);
                Check(new[]{grid.GapXMm,grid.GapYMm}.All(v=>double.IsFinite(v)&&v is >=0 and <=100000)); break;
            case LayoutCommand layout:
                Ids(layout.StaticIds); Check(layout.DryRun ? layout.ExpectedRevision is null : layout.ExpectedRevision is not null && System.Text.RegularExpressions.Regex.IsMatch(layout.ExpectedRevision,"^[a-f0-9]{64}$"));
                Check(new[]{layout.MarginLeftMm,layout.MarginRightMm,layout.MarginTopMm,layout.MarginBottomMm,layout.GapXMm,layout.GapYMm}.All(v=>double.IsFinite(v)&&v is >=0 and <=100000)); break;
            case CreateShapesCommand create:
                Id(create.LayerId); Check(create.Items.Length is >= 1 and <= 100);
                foreach (var item in create.Items) switch (item)
                {
                    case RectangleItem r: Coord(r.XMm, r.YMm); OperationContracts.Dimension(r.WidthMm); OperationContracts.Dimension(r.HeightMm); break;
                    case EllipseItem e: Coord(e.XMm, e.YMm); OperationContracts.Dimension(e.WidthMm); OperationContracts.Dimension(e.HeightMm); break;
                    case LineItem l: Coord(l.X1Mm, l.Y1Mm, l.X2Mm, l.Y2Mm); break;
                }
                break;
            case TransformCommand transform:
                Ids(transform.StaticIds);
                switch (transform)
                {
                    case MoveCommand move: Coord(move.DxMm, move.DyMm); break;
                    case ScaleCommand scale: Coord(scale.AnchorXMm, scale.AnchorYMm); Check(double.IsFinite(scale.Sx) && double.IsFinite(scale.Sy) && scale.Sx is > 0 and <= 100000 && scale.Sy is > 0 and <= 100000); break;
                    case RotateCommand rotate: Coord(rotate.AnchorXMm, rotate.AnchorYMm); Check(double.IsFinite(rotate.AngleDeg) && Math.Abs(rotate.AngleDeg) <= 360000); break;
                    case FlipCommand flip: Coord(flip.AnchorXMm, flip.AnchorYMm); Check(flip.Axis is "horizontal" or "vertical"); break;
                }
                break;
            case DuplicateCommand duplicate: Ids(duplicate.StaticIds); OperationContracts.Range(duplicate.Copies, 1, 1000, "copies"); Check((long)duplicate.Copies * duplicate.StaticIds.Length <= 1000); Coord(duplicate.StepXMm, duplicate.StepYMm); break;
            case StyleCommand style:
                Ids(style.StaticIds); Check(style.Fill is not null || style.Outline is not null);
                if (style.Fill is UniformFill fill) Color(fill.Color);
                if (style.Outline is UniformOutline outline) { Color(outline.Color); OperationContracts.Dimension(outline.WidthMm); }
                break;
            case CreateLayerCommand create: Id(create.PageId); Name(create.Name); break;
            case RenameLayerCommand rename: Id(rename.LayerId); Name(rename.Name); break;
            case LayerFlagsCommand flags: Id(flags.LayerId); Check(flags.Visible is not null || flags.Editable is not null || flags.Printable is not null); break;
            case MoveToLayerCommand move: Id(move.LayerId); Ids(move.StaticIds); break;
            case GroupCommand group: Ids(group.StaticIds); Check((group.Action == "group" && group.StaticIds.Length >= 2) || (group.Action == "ungroup" && group.StaticIds.Length == 1)); break;
            case BooleanCommand boolean: Check(boolean.Action is "weld" or "trim" or "intersect"); Check(boolean.LeftId > 0 && boolean.RightId > 0 && boolean.LeftId != boolean.RightId); break;
            case CreateTextCommand text:
                Id(text.LayerId); CorelText.Validate(text.Text); Name(text.FontFamily); Check(double.IsFinite(text.FontSizePt) && text.FontSizePt is > 0 and <= 10000);
                if (text is ArtisticTextCommand a) Coord(a.XMm, a.YMm);
                if (text is ParagraphTextCommand p) { Coord(p.XMm, p.YMm); OperationContracts.Dimension(p.WidthMm); OperationContracts.Dimension(p.HeightMm); }
                break;
            case ConvertCommand convert: Ids(convert.StaticIds); break;
        }
    }
}
