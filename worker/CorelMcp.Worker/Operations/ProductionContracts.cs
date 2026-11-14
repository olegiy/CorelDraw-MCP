using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CorelMcp.Worker.Validation;
using static CorelMcp.Worker.Operations.DrawingContracts;

namespace CorelMcp.Worker.Operations;

internal interface IReadOnlyDocumentArguments : IDocumentArguments;
internal sealed record InspectCurveArguments(string DocumentId, int PageIndex, int StaticId, int Offset = 0, int Limit = 100, int DeadlineSeconds = 600) : IReadOnlyDocumentArguments, IDeadlineArguments;
internal sealed record FindTextArguments(string DocumentId, int PageIndex, string Query, bool CaseSensitive, int Offset = 0, int Limit = 100,
    bool IncludeText = false, int SnippetLimit = 200, int MaxVisited = 10000, int DeadlineSeconds = 600) : IReadOnlyDocumentArguments, IDeadlineArguments;
internal sealed record PreflightArguments(string DocumentId, int PageIndex, int[] StaticIds, PreflightCheck[] Checks, int DeadlineSeconds = 600) : IReadOnlyDocumentArguments, IDeadlineArguments;
internal sealed record PointMm(double XMm, double YMm);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CurveLine), "line"), JsonDerivedType(typeof(CurveCubic), "cubic")]
internal abstract record CurveSegmentSpec(PointMm To);
internal sealed record CurveLine(PointMm To) : CurveSegmentSpec(To);
internal sealed record CurveCubic(PointMm To, PointMm Control1, PointMm Control2) : CurveSegmentSpec(To);
internal sealed record CurveSubpath(PointMm Start, CurveSegmentSpec[] Segments, bool Closed);
internal sealed record CreateCurveCommand(string LayerId, CurveSubpath[] Subpaths) : IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(MoveNodeCommand), "move_node"), JsonDerivedType(typeof(InsertNodeCommand), "insert_node"), JsonDerivedType(typeof(DeleteNodeCommand), "delete_node"),
 JsonDerivedType(typeof(SetNodeTypeCommand), "set_node_type"), JsonDerivedType(typeof(CloseSubpathCommand), "close_subpath"), JsonDerivedType(typeof(JoinEndpointsCommand), "join_endpoints")]
internal abstract record EditCurveCommand(int StaticId, string ExpectedRevision) : IDrawingCommand;
internal sealed record MoveNodeCommand(int StaticId, string ExpectedRevision, int SubpathIndex, int NodeIndex, double XMm, double YMm) : EditCurveCommand(StaticId, ExpectedRevision);
internal sealed record InsertNodeCommand(int StaticId, string ExpectedRevision, int SubpathIndex, int SegmentIndex, double T) : EditCurveCommand(StaticId, ExpectedRevision);
internal sealed record DeleteNodeCommand(int StaticId, string ExpectedRevision, int SubpathIndex, int NodeIndex) : EditCurveCommand(StaticId, ExpectedRevision);
internal sealed record SetNodeTypeCommand(int StaticId, string ExpectedRevision, int SubpathIndex, int NodeIndex, string NodeType) : EditCurveCommand(StaticId, ExpectedRevision);
internal sealed record CloseSubpathCommand(int StaticId, string ExpectedRevision, int SubpathIndex) : EditCurveCommand(StaticId, ExpectedRevision);
internal sealed record JoinEndpointsCommand(int StaticId, string ExpectedRevision, int FirstSubpathIndex, string FirstEnd, int SecondSubpathIndex, string SecondEnd, double ToleranceMm) : EditCurveCommand(StaticId, ExpectedRevision);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(ReplaceLiteralCommand), "replace_literal"), JsonDerivedType(typeof(SetTextCommand), "set_text"), JsonDerivedType(typeof(FormatTextCommand), "format")]
internal abstract record EditTextCommand(int[] StaticIds) : IDrawingCommand;
internal sealed record ReplaceLiteralCommand(int[] StaticIds, string Old, string New, bool CaseSensitive) : EditTextCommand(StaticIds);
internal sealed record SetTextCommand(int[] StaticIds, string Text) : EditTextCommand(StaticIds);
internal sealed record FormatTextCommand(int[] StaticIds, string? FontFamily = null, double? FontSizePt = null, bool? Bold = null, bool? Italic = null,
    string? Alignment = null, double? LineSpacingPercent = null) : EditTextCommand(StaticIds);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OpenContoursCheck), "open_contours"), JsonDerivedType(typeof(TextPresentCheck), "text_present"), JsonDerivedType(typeof(DimensionsCheck), "dimensions"),
 JsonDerivedType(typeof(OutlineWidthCheck), "outline_width"), JsonDerivedType(typeof(OutlineColorCheck), "outline_color")]
internal abstract record PreflightCheck;
internal sealed record OpenContoursCheck : PreflightCheck;
internal sealed record TextPresentCheck : PreflightCheck;
internal sealed record DimensionsCheck(double MinWidthMm, double MaxWidthMm, double MinHeightMm, double MaxHeightMm, double ToleranceMm) : PreflightCheck;
internal sealed record OutlineWidthCheck(double ExpectedMm, double ToleranceMm) : PreflightCheck;
internal sealed record OutlineColorCheck(ColorSpec Color, int Tolerance) : PreflightCheck;

internal static class ProductionContracts
{
    public static bool Supports(string tool) => tool is "corel_inspect_curve" or "corel_create_curve" or "corel_edit_curve" or "corel_edit_text" or "corel_find_text" or "corel_preflight";
    public static object Parse(string tool, JsonElement args)
    {
        try
        {
            Check(args.ValueKind == JsonValueKind.Object); RejectNull(args);
            object value;
            if (tool is "corel_create_curve" or "corel_edit_curve" or "corel_edit_text")
            {
                var fields = args.EnumerateObject().ToArray();
                string[] keys = ["request_id", "document_id", "page_index", "deadline_seconds"];
                var binding = Read<DrawingBinding>(JsonSerializer.SerializeToElement(fields.Where(p => keys.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value)));
                OperationContracts.Require(binding.RequestId, 200, "request_id");
                var payload = JsonSerializer.SerializeToElement(fields.Where(p => !keys.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value));
                IDrawingCommand command = tool switch { "corel_create_curve" => Read<CreateCurveCommand>(payload), "corel_edit_curve" => Read<EditCurveCommand>(payload), _ => Read<EditTextCommand>(payload) };
                Validate(command); OperationContracts.Page(binding.PageIndex);
                value = new DrawingArguments(binding.RequestId, binding.DocumentId, binding.PageIndex, command, binding.DeadlineSeconds);
            }
            else value = tool switch { "corel_inspect_curve" => Read<InspectCurveArguments>(args), "corel_find_text" => Read<FindTextArguments>(args), _ => Read<PreflightArguments>(args) };
            OperationContracts.Require(((IDocumentArguments)value).DocumentId, 100, "document_id");
            OperationContracts.Range(((IDeadlineArguments)value).DeadlineSeconds, 1, 3600, "deadline_seconds");
            switch (value)
            {
                case InspectCurveArguments curve: OperationContracts.Page(curve.PageIndex); Check(curve.StaticId > 0); OperationContracts.Range(curve.Offset, 0, 10000, "offset"); OperationContracts.Range(curve.Limit, 1, 500, "limit"); break;
                case FindTextArguments find:
                    OperationContracts.Page(find.PageIndex); Check(find.Query.Length > 0); CorelText.Validate(find.Query);
                    OperationContracts.Range(find.Offset, 0, 50000, "offset"); OperationContracts.Range(find.Limit, 1, 500, "limit");
                    OperationContracts.Range(find.SnippetLimit, 1, 1000, "snippet_limit"); OperationContracts.Range(find.MaxVisited, 100, 50000, "max_visited"); break;
                case PreflightArguments preflight:
                    OperationContracts.Page(preflight.PageIndex); Ids(preflight.StaticIds); Check(preflight.Checks.Length is >= 1 and <= 20);
                    foreach (var check in preflight.Checks) switch (check)
                    {
                        case DimensionsCheck d: Nonnegative(d.MinWidthMm, d.MaxWidthMm, d.MinHeightMm, d.MaxHeightMm, d.ToleranceMm); Check(d.MinWidthMm <= d.MaxWidthMm && d.MinHeightMm <= d.MaxHeightMm); break;
                        case OutlineWidthCheck w: Nonnegative(w.ExpectedMm, w.ToleranceMm); break;
                        case OutlineColorCheck color: Color(color.Color); OperationContracts.Range(color.Tolerance, 0, color.Color is CmykSpec ? 100 : 255, "tolerance"); break;
                    }
                    break;
            }
            return value;
        }
        catch (JsonException) { throw new OperationFailure("INVALID_INPUT", "Параметры не соответствуют строгой производственной схеме."); }
        catch (TextValidationException error) { throw new OperationFailure(error.Code, error.Message); }
    }
    private static void Nonnegative(params double[] values) => Check(values.All(value => double.IsFinite(value) && value is >= 0 and <= 100000));
    private static void Index(params int[] values) => Check(values.All(value => value is >= 1 and <= 10000));
    private static void Point(PointMm point) => Coord(point.XMm, point.YMm);
    private static void Validate(IDrawingCommand command)
    {
        switch (command)
        {
            case CreateCurveCommand create:
                Id(create.LayerId); Check(create.Subpaths.Length is >= 1 and <= 5000);
                Check(create.Subpaths.Sum(path => (long)path.Segments.Length + 1) <= 10000);
                foreach (var path in create.Subpaths)
                {
                    Point(path.Start); Check(path.Segments.Length >= (path.Closed ? 2 : 1));
                    foreach (var segment in path.Segments) { Point(segment.To); if (segment is CurveCubic cubic) { Point(cubic.Control1); Point(cubic.Control2); } }
                }
                break;
            case EditCurveCommand edit:
                Check(edit.StaticId > 0 && Regex.IsMatch(edit.ExpectedRevision, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant));
                switch (edit)
                {
                    case MoveNodeCommand move: Index(move.SubpathIndex, move.NodeIndex); Coord(move.XMm, move.YMm); break;
                    case InsertNodeCommand insert: Index(insert.SubpathIndex, insert.SegmentIndex); Check(double.IsFinite(insert.T) && insert.T is > 0 and < 1); break;
                    case DeleteNodeCommand delete: Index(delete.SubpathIndex, delete.NodeIndex); break;
                    case SetNodeTypeCommand type: Index(type.SubpathIndex, type.NodeIndex); Check(type.NodeType is "cusp" or "smooth" or "symmetrical"); break;
                    case CloseSubpathCommand close: Index(close.SubpathIndex); break;
                    case JoinEndpointsCommand join:
                        Index(join.FirstSubpathIndex, join.SecondSubpathIndex); Check(join.FirstEnd is "start" or "end" && join.SecondEnd is "start" or "end"); Nonnegative(join.ToleranceMm);
                        Check(join.FirstSubpathIndex != join.SecondSubpathIndex || join.FirstEnd != join.SecondEnd); break;
                }
                break;
            case EditTextCommand text:
                Ids(text.StaticIds);
                switch (text)
                {
                    case ReplaceLiteralCommand replace: Check(replace.Old.Length > 0); CorelText.Validate(replace.Old); CorelText.Validate(replace.New); break;
                    case SetTextCommand set: CorelText.Validate(set.Text); break;
                    case FormatTextCommand format:
                        Check(format.FontFamily is not null || format.FontSizePt is not null || format.Bold is not null || format.Italic is not null || format.Alignment is not null || format.LineSpacingPercent is not null);
                        if (format.FontFamily is not null) Name(format.FontFamily);
                        if (format.FontSizePt is not null) Check(double.IsFinite(format.FontSizePt.Value) && format.FontSizePt is > 0 and <= 10000);
                        if (format.Alignment is not null) Check(format.Alignment is "left" or "center" or "right" or "justify" or "full_justify");
                        if (format.LineSpacingPercent is not null) Check(double.IsFinite(format.LineSpacingPercent.Value) && format.LineSpacingPercent is > 0 and <= 1000);
                        break;
                }
                break;
        }
    }
}
