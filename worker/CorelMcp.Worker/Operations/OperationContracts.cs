using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CorelMcp.Worker.Operations;

internal sealed class OperationFailure(string code, string message, bool? applied = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool? Applied { get; } = applied;
}
internal interface IDeadlineArguments { int DeadlineSeconds { get; } }
internal interface IDocumentArguments { string DocumentId { get; } }
internal interface IRequestArguments { string RequestId { get; } }
internal sealed record StatusArguments(int DeadlineSeconds = 600) : IDeadlineArguments;
internal sealed record CreateDocumentArguments(string RequestId, double WidthMm = 210, double HeightMm = 297, int DeadlineSeconds = 600) : IRequestArguments, IDeadlineArguments;
internal sealed record OpenDocumentArguments(string RequestId, string Path, int DeadlineSeconds = 600) : IRequestArguments, IDeadlineArguments;
internal sealed record SaveDocumentArguments(string RequestId, string DocumentId, string? Path = null, int DeadlineSeconds = 600) : IRequestArguments, IDocumentArguments, IDeadlineArguments;
internal sealed record CloseDocumentArguments(string RequestId, string DocumentId, bool DiscardChanges = false, int DeadlineSeconds = 600) : IRequestArguments, IDocumentArguments, IDeadlineArguments;
internal sealed record InspectArguments(string DocumentId, int? PageIndex = null, int Offset = 0, int Limit = 100,
    bool Recursive = false, int MaxDepth = 8, int MaxVisited = 10000, bool IncludeText = false, int TextLimit = 1000,
    string? NameFilter = null, int[]? ShapeTypes = null, int[]? StaticIds = null, int DeadlineSeconds = 600) : IDocumentArguments, IDeadlineArguments;
internal sealed record PreviewArguments(string DocumentId, int? PageIndex = null, string? Target = null, string? Range = null,
    int MaxSize = 1200, int DeadlineSeconds = 600) : IDocumentArguments, IDeadlineArguments
{
    public string ResolveTarget() => Target ?? Range ?? "page";
}
internal sealed record ExportArguments(string RequestId, string DocumentId, string Path, string Format, string Range = "page",
    int? PageIndex = null, int Dpi = 150, string? ColorMode = null, int? JpegCompression = null, string? TiffCompression = null,
    bool? UseDocumentProfile = null, bool? Transparent = null, string? TextMode = null, int DeadlineSeconds = 600) : IRequestArguments, IDocumentArguments, IDeadlineArguments;
internal sealed record DescribeApiArguments(string Kind = "types", string? Name = null, string Filter = "", int Offset = 0, int Limit = 100, int DeadlineSeconds = 600) : IDeadlineArguments;
internal sealed record MeasureArguments(string DocumentId, int? PageIndex = null, int[]? StaticIds = null, double ToleranceMm = 0.01, int DeadlineSeconds = 600) : IDocumentArguments, IDeadlineArguments;

internal static class OperationContracts
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static object Parse(string tool, JsonElement args)
    {
        if(tool is "corel_create_batch" or "corel_update_batch")return Hybrid.NativeBatchContracts.Parse(tool,args);
        if (ProductionContracts.Supports(tool)) return ProductionContracts.Parse(tool, args);
        if (DrawingContracts.IsDrawingTool(tool)) return DrawingContracts.Parse(tool, args);
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(item => item.Value.ValueKind == JsonValueKind.Null))
            throw new OperationFailure("INVALID_INPUT", "Поля операции должны соответствовать схеме; null вместо отсутствующего optional-поля недопустим.");
        object value;
        try
        {
            value = tool switch
            {
                "corel_status" => Read<StatusArguments>(args),
                "corel_create_document" => Read<CreateDocumentArguments>(args),
                "corel_open_document" => Read<OpenDocumentArguments>(args),
                "corel_save_document" => Read<SaveDocumentArguments>(args),
                "corel_close_document" => Read<CloseDocumentArguments>(args),
                "corel_inspect_document" => Read<InspectArguments>(args),
                "corel_preview" => Read<PreviewArguments>(args),
                "corel_export" => Read<ExportArguments>(args),
                "corel_describe_api" => Read<DescribeApiArguments>(args),
                "corel_measure_shapes" => Read<MeasureArguments>(args),
                _ => throw new OperationFailure("UNKNOWN_TOOL", "Неподдерживаемый инструмент C#-исполнителя.")
            };
        }
        catch (JsonException) { throw new OperationFailure("INVALID_INPUT", "Параметры не соответствуют строгой схеме операции."); }
        if (value is IDeadlineArguments deadline) Range(deadline.DeadlineSeconds, 1, 3600, "deadline_seconds");
        if (value is IRequestArguments request) Require(request.RequestId, 200, "request_id");
        if (value is IDocumentArguments doc) Require(doc.DocumentId, 100, "document_id");
        switch (value)
        {
            case CreateDocumentArguments create:
                Dimension(create.WidthMm); Dimension(create.HeightMm); break;
            case OpenDocumentArguments open: AbsolutePath(open.Path); break;
            case SaveDocumentArguments save when save.Path is not null: AbsolutePath(save.Path); break;
            case InspectArguments inspect:
                Page(inspect.PageIndex); Range(inspect.Offset, 0, 10000000, "offset"); Range(inspect.Limit, 1, 500, "limit");
                Range(inspect.MaxDepth, 1, 32, "max_depth"); Range(inspect.MaxVisited, 100, 50000, "max_visited");
                Range(inspect.TextLimit, 1, 10000, "text_limit"); StaticIds(inspect.StaticIds);
                if (inspect.NameFilter?.Length > 200) Invalid("name_filter");
                if (inspect.ShapeTypes is not null && (inspect.ShapeTypes.Length is < 1 or > 100 || inspect.ShapeTypes.Any(item => item is < 0 or > 1000))) Invalid("shape_types");
                break;
            case PreviewArguments preview:
                Page(preview.PageIndex); Range(preview.MaxSize, 64, 2400, "max_size");
                if ((preview.Target is not null && preview.Target is not ("page" or "selection"))
                    || (preview.Range is not null && preview.Range is not ("page" or "selection"))
                    || (preview.Target is not null && preview.Range is not null && preview.Target != preview.Range)) Invalid("target/range");
                break;
            case ExportArguments export: ValidateExport(export); break;
            case MeasureArguments measure:
                Page(measure.PageIndex); StaticIds(measure.StaticIds);
                if (!double.IsFinite(measure.ToleranceMm) || measure.ToleranceMm is < 0.001 or > 1) Invalid("tolerance_mm");
                break;
            case DescribeApiArguments api:
                if (api.Kind is not ("types" or "members" or "enum") || (api.Kind != "types" && string.IsNullOrEmpty(api.Name))
                    || api.Offset is < 0 or > 10000000 || api.Limit is < 1 or > 200)
                    throw new OperationFailure("INVALID_API_QUERY", "Некорректные параметры запроса API.");
                foreach (var name in new[] { api.Name ?? "", api.Filter })
                    if (name.Length > 200 || (name.Length > 0 && !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)))
                        throw new OperationFailure("INVALID_NAME", "Допускаются только идентификаторы API без выражений и путей.");
                break;
        }
        return value;
    }

    private static T Read<T>(JsonElement args) => args.Deserialize<T>(Options) ?? throw new JsonException();
    private static void Invalid(string name) => throw new OperationFailure("INVALID_INPUT", "Некорректный параметр: " + name + ".");
    internal static void Require(string value, int maximum, string name) { if (string.IsNullOrEmpty(value) || value.Length > maximum) Invalid(name); }
    internal static void Range(int value, int minimum, int maximum, string name) { if (value < minimum || value > maximum) Invalid(name); }
    internal static void Dimension(double value) { if (!double.IsFinite(value) || value is <= 0 or > 100000) Invalid("размер в миллиметрах"); }
    internal static void Page(int? index) { if (index is <= 0) Invalid("page_index"); }
    internal static void StaticIds(int[]? ids)
    {
        if (ids is not null && (ids.Length is < 1 or > 100 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)) Invalid("static_ids");
    }
    internal static void AbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\0') || !System.IO.Path.IsPathFullyQualified(path)) Invalid("path");
    }
    private static void ValidateExport(ExportArguments export)
    {
        AbsolutePath(export.Path); Page(export.PageIndex);
        if (export.Format is not ("png" or "pdf" or "svg" or "jpg" or "jpeg" or "tiff" or "tif" or "ai" or "eps" or "dxf" or "plt"))
            throw new OperationFailure("UNSUPPORTED_FORMAT", "Неподдерживаемый формат экспорта.");
        if (export.Range is not ("page" or "selection" or "document") || (export.Range == "document" && export.Format != "pdf") || (export.Range == "selection" && export.Format == "pdf"))
            throw new OperationFailure("UNSUPPORTED_EXPORT_RANGE", "Диапазон не поддерживается форматом экспорта.");
        if (export.Dpi is < 1 or > 1200) throw new OperationFailure("INVALID_DPI", "DPI должен быть от 1 до 1200.");
        bool jpg = export.Format is "jpg" or "jpeg", tiff = export.Format is "tiff" or "tif";
        if ((export.ColorMode is not null && ((!jpg && !tiff) || export.ColorMode is not ("rgb" or "cmyk")))
            || (export.JpegCompression is not null && (!jpg || export.JpegCompression is < 0 or > 100))
            || (export.TiffCompression is not null && (!tiff || export.TiffCompression is not ("none" or "lzw")))
            || (export.Transparent is not null && !tiff) || (export.UseDocumentProfile is not null && !jpg && !tiff)
            || (export.TextMode is not null && (export.Format is not ("ai" or "eps") || export.TextMode is not ("preserve" or "curves")))) Invalid("параметры фильтра экспорта");
    }
}
