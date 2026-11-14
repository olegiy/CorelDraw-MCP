using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class ProductionContractsTests
{
    public static TheoryData<string, object> Valid => new()
    {
        { "corel_inspect_curve", new { static_id = 1 } },
        { "corel_create_curve", new { request_id = "once", layer_id = "layer", subpaths = new[] { new { start = new { x_mm = 0, y_mm = 0 }, closed = false, segments = new[] { new { kind = "line", to = new { x_mm = 10, y_mm = 10 } } } } } } },
        { "corel_edit_curve", new { request_id = "once", static_id = 1, expected_revision = new string('a', 64), action = "insert_node", subpath_index = 1, segment_index = 1, t = 0.5 } },
        { "corel_edit_text", new { request_id = "once", static_ids = new[] { 1 }, action = "replace_literal", old = "Ёж", @new = "Текст", case_sensitive = true } },
        { "corel_find_text", new { query = "Ёж", case_sensitive = false } },
        { "corel_preflight", new { static_ids = new[] { 1 }, checks = new object[] { new { kind = "outline_width", expected_mm = 0.2, tolerance_mm = 0.01 }, new { kind = "text_present" } } } },
    };
    [Theory, MemberData(nameof(Valid))]
    public void ProductionToolsRequireStrictBindingBeforeAnyCom(string tool, object payload)
    {
        var fields = Fields(payload); var parsed = OperationContracts.Parse(tool, JsonSerializer.SerializeToElement(fields));
        Assert.Equal("exact", Assert.IsAssignableFrom<IDocumentArguments>(parsed).DocumentId);
        Assert.Equal(600, Assert.IsAssignableFrom<IDeadlineArguments>(parsed).DeadlineSeconds);
        fields["expression"] = "anything";
        Assert.Equal("INVALID_INPUT", Assert.Throws<OperationFailure>(() => OperationContracts.Parse(tool, JsonSerializer.SerializeToElement(fields))).Code);
        fields.Remove("expression"); fields.Remove("page_index");
        Assert.Throws<OperationFailure>(() => OperationContracts.Parse(tool, JsonSerializer.SerializeToElement(fields)));
    }
    [Theory]
    [InlineData("corel_edit_curve", "\"request_id\":\"once\",\"static_id\":1,\"expected_revision\":\"stale\",\"action\":\"delete_node\",\"subpath_index\":1,\"node_index\":1")]
    [InlineData("corel_edit_curve", "\"request_id\":\"once\",\"static_id\":1,\"expected_revision\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"action\":\"insert_node\",\"subpath_index\":1,\"segment_index\":1,\"t\":0")]
    [InlineData("corel_preflight", "\"static_ids\":[1],\"checks\":[{\"kind\":\"outline_width\",\"expected_mm\":0.2}]")]
    [InlineData("corel_edit_text", "\"request_id\":\"once\",\"static_ids\":[1],\"action\":\"format\"")]
    [InlineData("corel_find_text", "\"query\":\"\",\"case_sensitive\":true")]
    public void InvalidRevisionsActionsAndIncompleteNumericRulesFailBeforeCom(string tool, string payload)
    {
        var error = Assert.Throws<OperationFailure>(() => OperationContracts.Parse(tool, JsonSerializer.Deserialize<JsonElement>("{\"document_id\":\"exact\",\"page_index\":1," + payload + "}")));
        Assert.Equal("INVALID_INPUT", error.Code); Assert.False(error.Applied);
    }
    private static Dictionary<string, object?> Fields(object payload)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(payload))!;
        fields["document_id"] = "exact"; fields["page_index"] = 1; return fields;
    }
}
