using CorelMcp.Worker.Hybrid;
using Xunit;
namespace CorelMcp.Worker.Tests;
[Collection("CorelLive")]
public sealed class LiveHybridGuardTests:IDisposable
{
    public void Dispose()=>LiveNativeBatchTests.UnloadTestRuntime();
    [LiveCorelFact]
    public async Task LastMissingTargetLockedLeafAndLockedAncestorRejectTheWholeBatch()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();var state=await c.Inspect(doc);
        var layer=state.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made=await c.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="rectangle",item_key="r"+i,x_mm=i*8,y_mm=10,width_mm=5,height_mm=4}).ToArray()});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        async Task Rejected(int[] targets,string code)
        {
            var before=await c.Inspect(doc);var error=await c.DrawFailure(doc,"corel_update_batch",new {items=targets.Select(id=>new {static_id=id,transform=new {action="move",dx_mm=5,dy_mm=3}}).ToArray()});
            Assert.Equal(code,error.Error!.Code);Assert.False(error.Error.Applied);Assert.Equal(before.GetRawText(),(await c.Inspect(doc)).GetRawText());
        }
        await Rejected(ids[..19].Append(2000000000).ToArray(),"SHAPE_NOT_FOUND");
        await c.Ok("test_lock_shape",new {document_id=doc,static_id=ids[19],locked=true});await Rejected(ids,"LOCKED_TARGET");
        await c.Ok("test_lock_shape",new {document_id=doc,static_id=ids[19],locked=false});
        var group=await c.Draw(doc,"corel_group_shapes",new {action="group",static_ids=ids[..2]});int groupId=group.GetProperty("created_static_ids")[0].GetInt32();
        await c.Ok("test_lock_shape",new {document_id=doc,static_id=groupId,locked=true});await Rejected([ids[0]],"LOCKED_TARGET");
        var control=await c.Create();var controlBefore=await c.Inspect(control);
        var wrongLayer=await c.DrawFailure(control,"corel_create_batch",new {layer_id=layer,items=new[]{new {kind="rectangle",item_key="wrong",x_mm=0,y_mm=0,width_mm=5,height_mm=5}}});
        Assert.False(wrongLayer.Error!.Applied);Assert.Equal(controlBefore.GetRawText(),(await c.Inspect(control)).GetRawText());
    }
    [LiveCorelFact]
    public async Task NativeBatchesKeepPhysicalMillimetersWithShiftedOriginAndInchCdrReopen()
    {
        foreach(string unit in new[]{"mm","inch"})
        {
            using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();await c.Ok("test_configure",new {document_id=doc,unit});
            var state=await c.Inspect(doc);var layer=state.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            var made=await c.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="rectangle",item_key="r"+i,x_mm=10+i*8,y_mm=10,width_mm=5,height_mm=4}).ToArray()});
            for(int i=0;i<20;i++)Assert.Equal(10+i*8,made.GetProperty("items")[i].GetProperty("bounds_mm").GetProperty("left").GetDouble(),3);
            string path=Path.Combine(c.Directory,"hybrid-"+unit+".cdr");await c.Ok("corel_save_document",new {request_id=Guid.NewGuid().ToString(),document_id=doc,path});
            await c.Ok("corel_close_document",new {request_id=Guid.NewGuid().ToString(),document_id=doc,discard_changes=false});
            var reopened=await c.Ok("corel_open_document",new {request_id=Guid.NewGuid().ToString(),path});string actualDoc=reopened.GetProperty("document_id").GetString()!;
            var read=await c.Inspect(actualDoc);var ids=read.GetProperty("shapes").EnumerateArray().OrderBy(s=>s.GetProperty("bounds_mm").GetProperty("left").GetDouble()).Select(s=>s.GetProperty("static_id").GetInt32()).ToArray();
            var moved=await c.Draw(actualDoc,"corel_update_batch",new {items=ids.Select(id=>new {static_id=id,transform=new {action="move",dx_mm=5,dy_mm=3}}).ToArray()});
            for(int i=0;i<20;i++) {var b=moved.GetProperty("items")[i].GetProperty("bounds_mm");Assert.Equal(15+i*8,b.GetProperty("left").GetDouble(),3);Assert.Equal(13,b.GetProperty("bottom").GetDouble(),3);Assert.Equal(5,b.GetProperty("width").GetDouble(),3);}
            await c.Ok("corel_close_document",new {request_id=Guid.NewGuid().ToString(),document_id=actualDoc,discard_changes=true});
        }
    }
}
