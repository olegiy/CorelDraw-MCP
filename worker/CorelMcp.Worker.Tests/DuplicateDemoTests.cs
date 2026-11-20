using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class DuplicateDemoTests
{
    [Fact]
    public void TypedRequestAcceptsOneShapeAndAnIntegerCount()
    {
        using var json = JsonDocument.Parse("""
            {"request_id":"test","document_id":"doc","page_index":1,"macro_id":"duplicate_demo","static_ids":[4],"parameters":{"count":3}}
            """);
        var args = Assert.IsType<RunMacroArguments>(OperationContracts.Parse("corel_run_macro", json.RootElement));
        Assert.Equal(3, args.Parameters.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("2.5")]
    [InlineData("null")]
    public void InvalidCountsAreRejectedBeforeCom(string count)
    {
        const string template = """
            {"request_id":"test","document_id":"doc","page_index":1,"macro_id":"duplicate_demo","static_ids":[4],"parameters":{"count":COUNT}}
            """;
        using var json = JsonDocument.Parse(template.Replace("COUNT", count, StringComparison.Ordinal));
        Assert.Equal("INVALID_INPUT",
            Assert.Throws<OperationFailure>(() => OperationContracts.Parse("corel_run_macro", json.RootElement)).Code);
    }

    [Fact]
    public void ReadsCompletedAndPartialVbaResults()
    {
        Assert.Equal(new[] { 5, 6, 7 }, DuplicateDemo.ReadReply("n|OK|0|5,6,7", "n", 3).Ids);
        var failed = DuplicateDemo.ReadReply("n|ERROR|5|5", "n", 3);
        Assert.Equal(5, failed.ErrorNumber);
        Assert.Equal(new[] { 5 }, failed.Ids);
    }

    [Theory]
    [InlineData("wrong|OK|0|5,6,7")]
    [InlineData("n|OK|0|5,5,7")]
    [InlineData("n|OK|0|5,6")]
    [InlineData("n|OK|0|5,6,7,8")]
    [InlineData("n|ERROR|0|")]
    public void RejectsUncorrelatedOrInconsistentVbaResults(string raw)
    {
        Assert.Throws<InvalidDataException>(() => DuplicateDemo.ReadReply(raw, "n", 3));
    }
}
