using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class OperationContractsTests
{
    [Fact]
    public void ExistingDocumentDefaultsRemainCompatible()
    {
        var create = Assert.IsType<CreateDocumentArguments>(Parse("corel_create_document", "{\"request_id\":\"once\"}"));
        Assert.Equal(210, create.WidthMm);
        Assert.Equal(297, create.HeightMm);
        var inspect = Assert.IsType<InspectArguments>(Parse("corel_inspect_document", "{\"document_id\":\"exact\"}"));
        Assert.Equal(100, inspect.Limit);
        Assert.False(inspect.IncludeText);
        Assert.False(inspect.Recursive);
        var preview = Assert.IsType<PreviewArguments>(Parse("corel_preview", "{\"document_id\":\"exact\",\"range\":\"selection\"}"));
        Assert.Equal("selection", preview.ResolveTarget());
        Assert.Equal(1200, preview.MaxSize);
    }

    [Theory]
    [InlineData("corel_create_document", "{\"request_id\":\"once\",\"width_mm\":0}")]
    [InlineData("corel_create_document", "{\"request_id\":\"once\",\"height_mm\":100001}")]
    [InlineData("corel_create_document", "{}")]
    [InlineData("corel_status", "{\"surprise\":true}")]
    [InlineData("corel_inspect_document", "{\"document_id\":\"exact\",\"limit\":501}")]
    [InlineData("corel_inspect_document", "{\"document_id\":\"exact\",\"static_ids\":[1,1]}")]
    [InlineData("corel_inspect_document", "{\"document_id\":\"exact\",\"page_index\":0}")]
    [InlineData("corel_inspect_document", "{\"document_id\":\"exact\",\"page_index\":null}")]
    [InlineData("corel_measure_shapes", "{\"document_id\":\"exact\",\"tolerance_mm\":0.0001}")]
    [InlineData("corel_preview", "{\"document_id\":\"exact\",\"target\":\"page\",\"range\":\"selection\"}")]
    [InlineData("corel_close_document", "{\"request_id\":\"once\"}")]
    [InlineData("corel_open_document", "{\"request_id\":\"once\",\"path\":\"relative.cdr\"}")]
    public void InvalidArgumentsFailBeforeComOrAnyMutation(string tool, string json)
    {
        var failure = Assert.Throws<OperationFailure>(() => Parse(tool, json));
        Assert.False(failure.Applied);
    }

    [Theory]
    [InlineData("\"range\":\"document\"", "png")]
    [InlineData("\"range\":\"selection\"", "pdf")]
    [InlineData("\"jpeg_compression\":80", "svg")]
    [InlineData("\"transparent\":true", "jpg")]
    [InlineData("\"dpi\":0", "png")]
    public void ExportOptionsCannotReachTheWrongFilter(string option, string format)
        => Assert.Throws<OperationFailure>(() => Parse("corel_export", "{\"request_id\":\"once\",\"document_id\":\"exact\",\"path\":\"C:\\\\out." + format + "\",\"format\":\"" + format + "\"," + option + "}"));

    [Fact]
    public void ApiQueriesNeverAcceptExpressionsAndLegacyCodeHasNoDispatchContract()
    {
        Assert.Equal("INVALID_NAME", Assert.Throws<OperationFailure>(() => Parse("corel_describe_api", "{\"kind\":\"members\",\"name\":\"Application.Quit\"}")).Code);
        Assert.Equal("UNKNOWN_TOOL", Assert.Throws<OperationFailure>(() => Parse("corel_execute_vba", "{}")).Code);
        Assert.Equal("UNKNOWN_TOOL", Assert.Throws<OperationFailure>(() => Parse("corel_build_vba", "{}")).Code);
    }

    private static object Parse(string tool, string json) => OperationContracts.Parse(tool, JsonSerializer.Deserialize<JsonElement>(json));
}
