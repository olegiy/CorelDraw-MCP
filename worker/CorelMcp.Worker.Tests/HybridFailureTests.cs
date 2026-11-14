using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Operations;
using Xunit;
namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class HybridFailureTests : IDisposable
{
    public void Dispose()=>LiveNativeBatchTests.UnloadTestRuntime();
    [LiveCorelFact]
    public async Task LiteralReplacementReportsOnlyMatchingTargets()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();
        string layer=(await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made=await c.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="artistic_text",item_key="t"+i,text=i==0?"hit":"miss",x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12}).ToArray()});
        var ids=made.GetProperty("items").EnumerateArray().Select(v=>v.GetProperty("static_id").GetInt32()).ToArray();
        var result=await c.Draw(doc,"corel_edit_text",new {static_ids=ids,action="replace_literal",old="hit",@new="done",case_sensitive=true});
        Assert.Equal(ids[0],Assert.Single(result.GetProperty("affected_static_ids").EnumerateArray()).GetInt32());Assert.Single(result.GetProperty("items").EnumerateArray());
        var actual=(await c.Inspect(doc)).GetProperty("shapes").EnumerateArray().ToDictionary(s=>s.GetProperty("static_id").GetInt32());
        for(int i=0;i<20;i++)Assert.Equal(i==0?"done":"miss",actual[ids[i]].GetProperty("text").GetString());
    }
    private sealed class FaultRuntime(bool loseReply,bool updateOnly=false,bool geometryFail=false,string fault=""):INativeVbaRuntime
    {
        private readonly NativeVbaRuntime actual=new(Path.Combine(AppContext.BaseDirectory,"native"));
        public NativeModuleIdentity? LastIdentity=>actual.LastIdentity;
        public string ExtractGeometry(OperationContext c,VGCore.Document doc,VGCore.ShapeRange targets,int[] ids,double mm,string nonce)=>geometryFail
            ?throw new OperationFailure("TEST_NATIVE_GEOMETRY","Тестовый отказ private geometry.")
            :actual.ExtractGeometry(c,doc,targets,ids,mm,nonce);
        public NativeModuleIdentity EnsureReady(OperationContext c)=>actual.EnsureReady(c);
        public void RequireOperation(string operation)=>actual.RequireOperation(operation);
        public void ValidateFonts(OperationContext c,string[] names)=>actual.ValidateFonts(c,names);
        public NativeBatchReply Apply(PreparedNativeBatch batch)
        {
            if(fault=="cleanup")
            {
                var completed=actual.Apply(batch);
                batch.Context.Cleanup.Add("native.events",new InvalidOperationException("Тестовый отказ восстановления events."));
                return completed;
            }
            if(fault.StartsWith("pending_",StringComparison.Ordinal))
            {
                if(fault=="pending_text"&&batch.Operation!="update_batch")return actual.Apply(batch);
                int width=batch.Rows.GetLength(1);var broken=new object[3,width];for(int i=0;i<3;i++)for(int j=0;j<width;j++)broken[i,j]=batch.Rows[i,j];
                if(fault=="pending_create")broken[2,4]="invalid-number";
                else if(fault=="pending_update_first")broken[2,11]="invalid-number";
                else if(fault=="pending_update_second")broken[2,20]="invalid-number";
                else if(fault=="pending_text")broken[2,34]=double.MaxValue;
                return actual.Apply(batch with {Rows=broken,Keys=batch.Keys[..3],OriginalIds=batch.OriginalIds.Take(3).ToArray()});
            }
            if(updateOnly&&batch.Operation!="update_batch")return actual.Apply(batch);
            if(loseReply)
            {
                _=actual.Apply(batch);
                batch.Context.Session.MarkNativeOutcomeUnknown();
                throw new OperationFailure("NATIVE_OUTCOME_UNKNOWN","Тестовая потеря ответа после завершённого native call.",null);
            }
            int columns=batch.Rows.GetLength(1);var rows=new object[2,columns];for(int i=0;i<2;i++)for(int j=0;j<columns;j++)rows[i,j]=batch.Rows[i,j];
            var reply=actual.Apply(batch with {Rows=rows,Keys=batch.Keys[..2],OriginalIds=batch.OriginalIds.Take(2).ToArray()});
            return reply with {Ok=false,Applied=fault=="convert_unknown"?null:reply.Applied,Partial=true,ErrorCode="TEST_PARTIAL",ErrorMessage="Тестовый отказ после двух подтверждённых строк."};
        }
        public void Dispose()=>actual.Dispose();
    }

    [LiveCorelFact]
    public async Task CleanupFailureCannotReportSuccessfulBatchAndPreservesKnownIds()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(false,fault:"cleanup"));var doc=await c.Create();
        string layer=(await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var result=await c.Call("corel_create_batch",new {request_id=Guid.NewGuid().ToString(),document_id=doc,page_index=1,layer_id=layer,items=Enumerable.Range(0,5).Select(i=>new {item_key="r"+i,kind="rectangle",x_mm=i*10,y_mm=0,width_mm=5,height_mm=5}).ToArray()});
        Assert.False(result.Ok);Assert.Equal("CLEANUP_FAILED",result.Error!.Code);Assert.True(result.Error.Applied);Assert.Equal(false,result.Cleanup!["completed"]);
        var data=System.Text.Json.JsonSerializer.SerializeToElement(result.Result,Protocol.JsonOptions);Assert.Equal(5,data.GetProperty("created_static_ids").GetArrayLength());Assert.Equal(5,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }

    [LiveCorelFact]
    public async Task UnknownConversionRetainsEarlierConfirmedIdsAndMappings()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(false,updateOnly:false,fault:"convert_unknown"));var doc=await c.Create();
        string layer=(await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        // Individual C# creations bypass the native fault adapter.
        var ids=new List<int>();for(int i=0;i<20;i++)ids.Add((await c.Draw(doc,"corel_create_text",new {kind="artistic",layer_id=layer,text="Ёж",x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12})).GetProperty("created_static_ids")[0].GetInt32());
        var failed=await c.DrawFailure(doc,"corel_convert_to_curves",new {static_ids=ids});
        try
        {
            Assert.Equal("NATIVE_OUTCOME_UNKNOWN",failed.Error!.Code);Assert.Null(failed.Error.Applied);
            var data=System.Text.Json.JsonSerializer.SerializeToElement(failed.Result,Protocol.JsonOptions);
            Assert.Equal(2,data.GetProperty("id_mappings").GetArrayLength());Assert.True(data.GetProperty("affected_static_ids").GetArrayLength()>=2);
            var after=(await c.Inspect(doc)).GetProperty("shapes").EnumerateArray().Select(s=>s.GetProperty("static_id").GetInt32()).ToArray();
            Assert.Equal(after.Except(ids).Order(),data.GetProperty("created_static_ids").EnumerateArray().Select(s=>s.GetInt32()).Order());
        }
        finally {await c.Ok("test_close_after_unknown",new {document_id=doc});}
    }

    [LiveCorelTheory]
    [InlineData("pending_create",2)]
    [InlineData("pending_update_first",2)]
    [InlineData("pending_update_second",3)]
    [InlineData("pending_text",2)]
    public async Task PendingMutatorIsUnknownWithoutLosingEarlierConfirmedTargets(string fault,int knownCount)
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(false,fault:fault));var doc=await c.Create();
        string layer=(await c.Inspect(doc)).GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        WorkerResult failed;
        if(fault=="pending_create")failed=await c.DrawFailure(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,5).Select(i=>new {item_key="r"+i,kind="rectangle",x_mm=i*10,y_mm=0,width_mm=5,height_mm=5}).ToArray()});
        else if(fault=="pending_text")
        {
            var ids=new List<int>();for(int i=0;i<20;i++)ids.Add((await c.Draw(doc,"corel_create_text",new {kind="artistic",layer_id=layer,text="Ёж",x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12})).GetProperty("created_static_ids")[0].GetInt32());
            failed=await c.DrawFailure(doc,"corel_edit_text",new {static_ids=ids,action="format",line_spacing_percent=120});
        }
        else
        {
            var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=Enumerable.Range(0,5).Select(i=>new {kind="rectangle",x_mm=i*10,y_mm=0,width_mm=5,height_mm=5}).ToArray()});
            failed=await c.DrawFailure(doc,"corel_update_batch",new {items=made.GetProperty("created_static_ids").EnumerateArray().Select(id=>new {static_id=id.GetInt32(),fill=new {kind="uniform",color=new {model="rgb",r=10,g=20,b=30}},outline=new {kind="uniform",width_mm=.2,color=new {model="rgb",r=0,g=0,b=0}}}).ToArray()});
        }
        try
        {
            Assert.Equal("NATIVE_OUTCOME_UNKNOWN",failed.Error!.Code);Assert.Null(failed.Error.Applied);
            var data=System.Text.Json.JsonSerializer.SerializeToElement(failed.Result,Protocol.JsonOptions);
            Assert.Equal(knownCount,data.GetProperty("affected_static_ids").GetArrayLength());Assert.Empty(data.GetProperty("deleted_static_ids").EnumerateArray());
            var blocked=await c.DrawFailure(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=0,y_mm=30,width_mm=1,height_mm=1}}});Assert.Equal("NATIVE_SESSION_BLOCKED",blocked.Error!.Code);
        }
        finally {await c.Ok("test_close_after_unknown",new {document_id=doc});}
    }

    [LiveCorelFact]
    public async Task PrivateGeometryFailureReportsNativeRouteAndPreservesSource()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(false,true,true));
        var doc=await c.Create();var state=await c.Inspect(doc);var layer=state.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=0,y_mm=0,width_mm=10,height_mm=10}}});
        var before=await c.Inspect(doc);
        var failed=await c.Call("corel_measure_shapes",new {document_id=doc,static_ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray()});
        Assert.False(failed.Ok);Assert.Equal("TEST_NATIVE_GEOMETRY",failed.Error!.Code);Assert.False(failed.Error.Applied);
        Assert.Equal("vba",failed.Versions!["execution_engine"]);Assert.Equal(before.GetRawText(),(await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task PartialUpdateNeverReportsUnchangedTargetsAsDeleted()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(false,true));
        var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var created=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,5).Select(i=>new {item_key="r"+i,kind="rectangle",x_mm=i*10,y_mm=0,width_mm=5,height_mm=5}).ToArray()});
        var ids=created.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var result=await client.DrawFailure(doc,"corel_update_batch",new {items=ids.Select(id=>new {static_id=id,transform=new {action="move",dx_mm=1,dy_mm=0}}).ToArray()});
        Assert.True(result.Error!.Applied);
        var details=System.Text.Json.JsonSerializer.SerializeToElement(result.Result,Protocol.JsonOptions);
        Assert.Empty(details.GetProperty("deleted_static_ids").EnumerateArray());
        Assert.Equal(ids[..2],details.GetProperty("affected_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray());
        Assert.Equal(5,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }

    [LiveCorelFact]
    public async Task KnownPartialFailureKeepsConfirmedIdsAndUnknownBlocksNewMutations()
    {
        foreach(bool unknown in new[]{false,true})
        {
            using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid,()=>new FaultRuntime(unknown));
            var doc=await client.Create();var initial=await client.Inspect(doc);
            var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            var failed=await client.DrawFailure(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,5).Select(i=>new {item_key="r"+i,kind="rectangle",x_mm=i*10,y_mm=0,width_mm=5,height_mm=5}).ToArray()});
            if(unknown)
            {
                Assert.Equal("NATIVE_OUTCOME_UNKNOWN",failed.Error!.Code);Assert.Null(failed.Error.Applied);
                Assert.Equal(5,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
                var blocked=await client.DrawFailure(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=0,y_mm=20,width_mm=5,height_mm=5}}});
                Assert.Equal("NATIVE_SESSION_BLOCKED",blocked.Error!.Code);Assert.False(blocked.Error.Applied);
                Assert.Equal(5,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
                await client.Ok("test_close_after_unknown",new {document_id=doc});
            }
            else
            {
                Assert.True(failed.Error!.Applied);
                var data=System.Text.Json.JsonSerializer.SerializeToElement(failed.Result,Protocol.JsonOptions);
                Assert.True(data.GetProperty("partial").GetBoolean());Assert.Equal(2,data.GetProperty("created_static_ids").GetArrayLength());
                Assert.Equal(2,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
            }
        }
    }
}
