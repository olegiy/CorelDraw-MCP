using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class NativeBatchTests
{
    private static DrawingArguments Parse(string tool,string fields)=>NativeBatchContracts.Parse(tool,JsonDocument.Parse("{\"request_id\":\"once\",\"document_id\":\"exact\",\"page_index\":1,"+fields+"}").RootElement);
    [Fact] public void CreateUsesExistingStrictGeometryAndStyleSemantics()
    {
        var value=Parse("corel_create_batch","\"layer_id\":\"layer\",\"items\":[{\"item_key\":\"r\",\"kind\":\"rectangle\",\"x_mm\":0,\"y_mm\":0,\"width_mm\":12,\"height_mm\":8}]");
        var batch=Assert.IsType<CreateBatchCommand>(value.Command);
        Assert.Equal("r",batch.Items[0].ItemKey);Assert.IsType<CreateShapesCommand>(batch.Items[0].Creation);
    }
    [Theory]
    [InlineData("{\"static_id\":4}")]
    [InlineData("{\"static_id\":4,\"transform\":{\"action\":\"run\"}}")]
    [InlineData("{\"static_id\":4,\"fill\":null}")]
    public void InvalidUpdateDoesNotReachNativeCode(string item)
    {Assert.Throws<OperationFailure>(()=>Parse("corel_update_batch","\"items\":["+item+"]"));}
    [Fact] public void DuplicateIdsAndUnknownTopLevelFieldsAreRejected()
    {
        const string item="{\"static_id\":4,\"transform\":{\"action\":\"move\",\"dx_mm\":5,\"dy_mm\":3}}";
        Assert.Throws<OperationFailure>(()=>Parse("corel_update_batch","\"items\":["+item+","+item+"]"));
        Assert.Throws<OperationFailure>(()=>Parse("corel_update_batch","\"items\":["+item+"],\"macro\":\"Other.Main\""));
        var batch=Assert.IsType<UpdateBatchCommand>(Parse("corel_update_batch","\"items\":["+item+"]").Command);
        Assert.Equal(4,batch.Items[0].StaticId);Assert.IsType<MoveCommand>(batch.Items[0].Transform);
    }
}
