using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class HybridPolicyTests
{
    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    public void MoveUsesExplicitThreshold(int count, bool native)
    {
        var command = new MoveCommand(Enumerable.Range(1, count).ToArray(), 5, 3);
        Assert.Equal(native ? ExecutionEngine.Vba : ExecutionEngine.CSharp, HybridPolicy.Select(command, new(count, true, false), ExecutionMode.Hybrid).Engine);
        Assert.Equal(ExecutionEngine.CSharp, HybridPolicy.Select(command, new(count, true, false), ExecutionMode.CSharp).Engine);
    }

    [Fact]
    public void UnsupportedStructureAndUnmeasuredOperationsStayOnCSharp()
    {
        Assert.Equal(ExecutionEngine.CSharp, HybridPolicy.Select(new MoveCommand(Enumerable.Range(1, 20).ToArray(), 5, 3), new(20, false, false), ExecutionMode.Hybrid).Engine);
        Assert.Equal(ExecutionEngine.CSharp, HybridPolicy.Select(new RotateCommand(Enumerable.Range(1, 20).ToArray(), 30, 0, 0), new(20, true, false), ExecutionMode.Hybrid).Engine);
        Assert.Equal(ExecutionEngine.CSharp, HybridPolicy.Select(new GroupCommand("group", [1, 2]), new(20, true, false), ExecutionMode.Hybrid).Engine);
    }

    [Fact]
    public void DuplicationThresholdCountsNewShapesRatherThanSources()
    {
        var command = new DuplicateCommand([1], 20, 1, 1);
        var decision = HybridPolicy.Select(command, new(1, true, false, 20), ExecutionMode.Hybrid);
        Assert.Equal(ExecutionEngine.Vba, decision.Engine);
        Assert.Equal("duplicate", decision.Operation);
        Assert.Equal("hybrid-v1", decision.PolicyVersion);
    }

    [Fact]
    public void TextConversionRequiresTextLeaves()
    {
        var command = new ConvertCommand(Enumerable.Range(1, 20).ToArray());
        Assert.Equal(ExecutionEngine.CSharp, HybridPolicy.Select(command, new(20, true, false), ExecutionMode.Hybrid).Engine);
        Assert.Equal(ExecutionEngine.Vba, HybridPolicy.Select(command, new(20, true, true), ExecutionMode.Hybrid).Engine);
    }

    [Fact]
    public void ExplicitBatchNeverSilentlyUsesAnotherEngine()
    {
        var command = new UpdateBatchCommand([new(4, new NoFill(), null, null)]);
        Assert.Equal(ExecutionEngine.Vba,HybridPolicy.Select(command,new(1,true,false),ExecutionMode.Hybrid).Engine);
        Assert.Throws<OperationFailure>(()=>HybridPolicy.Select(command,new(1,true,false),ExecutionMode.CSharp));
        Assert.Throws<OperationFailure>(()=>HybridPolicy.Select(command,new(1,false,false),ExecutionMode.Hybrid));
    }

    [Fact]
    public void CreateStyleAndTextUseMeasuredEligibleFamilies()
    {
        var ids = Enumerable.Range(1, 20).ToArray();
        IDrawingCommand[] commands = [
            new CreateShapesCommand("exact-layer", Enumerable.Range(1, 20).Select(_ => (ShapeItem)new RectangleItem(0, 0, 12, 8)).ToArray()),
            new StyleCommand(ids, new UniformFill(new RgbSpec(40, 120, 210))),
            new ReplaceLiteralCommand(ids, "ёж", "лис", false),
            new SetTextCommand(ids, "Текст"), new FormatTextCommand(ids, FontSizePt: 16)
        ];
        foreach (var command in commands)
            Assert.Equal(ExecutionEngine.Vba, HybridPolicy.Select(command, new(20, true, true), ExecutionMode.Hybrid).Engine);
        Assert.Throws<ArgumentOutOfRangeException>(() => HybridPolicy.Select(commands[0], new(20, true, true), (ExecutionMode)99));
    }

    [Fact]
    public void TargetSummaryCannotInventTargetsOrCopies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetSummary(-1, true, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetSummary(1, true, false, -1));
        Assert.Throws<ArgumentException>(() => HybridPolicy.Select(new MoveCommand([1], 1, 1), new(20, true, false), ExecutionMode.Hybrid));
    }
}
