using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker.Hybrid;

internal enum ExecutionMode { CSharp, Hybrid }
internal enum ExecutionEngine { CSharp, Vba }
internal sealed record RouteDecision(ExecutionEngine Engine, string Operation, string PolicyVersion);

// Constructed only from the operation's completed, guarded preflight.
internal sealed record TargetSummary
{
    public int Count { get; }
    public bool SupportedLeaves { get; }
    public bool AllText { get; }
    public int NewCopies { get; }
    public TargetSummary(int count, bool supportedLeaves, bool allText, int newCopies = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(newCopies);
        Count = count; SupportedLeaves = supportedLeaves; AllText = allText; NewCopies = newCopies;
    }
}

internal static class HybridPolicy
{
    public const string Version = "hybrid-v1";
    public static RouteDecision Select(IDrawingCommand command, TargetSummary targets, ExecutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(targets);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        int? expected = command switch
        {
            CreateShapesCommand c => c.Items.Length, TransformCommand c => c.StaticIds.Length,
            StyleCommand c => c.StaticIds.Length, DuplicateCommand c => c.StaticIds.Length,
            EditTextCommand c => c.StaticIds.Length, ConvertCommand c => c.StaticIds.Length,
            CreateBatchCommand c => c.Items.Length, UpdateBatchCommand c => c.Items.Length,
            _ => null
        };
        if (expected is not null && expected != targets.Count)
            throw new ArgumentException("Сводка целей не соответствует проверенной команде.", nameof(targets));
        if(command is CreateBatchCommand or UpdateBatchCommand)
        {
            if(mode!=ExecutionMode.Hybrid)throw new OperationFailure("HYBRID_REQUIRED","Пакетная операция требует включённого проверенного VBA runtime.");
            if(!targets.SupportedLeaves)throw new OperationFailure("UNSUPPORTED_SHAPE","Пакет содержит неподдержанные цели.");
            return new(ExecutionEngine.Vba,command is CreateBatchCommand?"create_batch":"update_batch",Version);
        }
        string operation = command switch
        {
            CreateShapesCommand => "create_shapes", StyleCommand => "style", MoveCommand => "move",
            DuplicateCommand => "duplicate", ReplaceLiteralCommand => "replace_literal",
            SetTextCommand => "set_text", FormatTextCommand => "format_text", ConvertCommand => "convert_text",
            _ => "csharp"
        };
        bool eligible = targets.SupportedLeaves && operation != "csharp"
            && (command is DuplicateCommand ? targets.NewCopies >= 20 : targets.Count >= 20)
            && (command is not (EditTextCommand or ConvertCommand) || targets.AllText);
        return new(mode == ExecutionMode.Hybrid && eligible ? ExecutionEngine.Vba : ExecutionEngine.CSharp, operation, Version);
    }
}
