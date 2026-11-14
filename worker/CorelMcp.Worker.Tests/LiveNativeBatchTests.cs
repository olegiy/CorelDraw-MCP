using CorelMcp.Worker.Hybrid;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveNativeBatchTests : IDisposable
{
    [LiveCorelFact]
    public async Task NativeGeometryLimitReturnsStructuredErrorWithoutModalOrSourceChanges()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();var before=await c.Inspect(doc);
        var result=await c.Ok("test_native_geometry_error",new {document_id=doc});
        Assert.Equal("GEOMETRY_LIMIT_EXCEEDED",result.GetProperty("error_code").GetString());
        Assert.Equal(before.GetRawText(),(await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task NativeFontPreflightRejectsMissingLastFontBeforeCreatingAnything()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();var initial=await c.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var failed=await c.DrawFailure(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="artistic_text",item_key="t"+i,text="Ёж",x_mm=i,y_mm=10,font_family=i==19?"CorelMcp_Missing_Font_9f32b1":"Arial",font_size_pt=12}).ToArray()});
        Assert.Equal("FONT_NOT_INSTALLED",failed.Error!.Code);Assert.False(failed.Error.Applied);
        Assert.Equal(0,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }

    [LiveCorelFact]
    public async Task NativeLiteralReplacementPreservesMixedFormattingOutsideMatches()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await c.Create();var initial=await c.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var made=await c.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="artistic_text",item_key="t"+i,text="АА ёж ББ ёж",x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12}).ToArray()});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        foreach(int id in ids)await c.Ok("test_text_mixed",new {document_id=doc,static_id=id});
        var replaced=await c.Draw(doc,"corel_edit_text",new {static_ids=ids,action="replace_literal",old="ёж",@new="ЛИС",case_sensitive=true});
        foreach(var item in replaced.GetProperty("items").EnumerateArray())Assert.Equal("АА ЛИС ББ ЛИС",item.GetProperty("text").GetString());
        foreach(int id in ids)
        {
            Assert.True((await c.Ok("test_text_character",new {document_id=doc,static_id=id,index=1})).GetProperty("bold").GetBoolean());
            Assert.True((await c.Ok("test_text_character",new {document_id=doc,static_id=id,index=8})).GetProperty("italic").GetBoolean());
        }
    }

    [LiveCorelFact]
    public async Task NativeTextConversionReportsActualTreesAndPreservesGeometry()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var created=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="artistic_text",item_key="t"+i,text="Ёж",x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12}).ToArray()});
        var ids=created.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var converted=await client.Draw(doc,"corel_convert_to_curves",new {static_ids=ids});
        Assert.Equal("vba",converted.GetProperty("execution_engine").GetString());
        var mappings=converted.GetProperty("id_mappings").EnumerateArray().ToArray();Assert.Equal(20,mappings.Length);
        var observed=converted.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(20,observed.Length);
        for(int i=0;i<20;i++)
        {
            Assert.Equal(ids[i],mappings[i].GetProperty("source_static_id").GetInt32());
            Assert.Contains(observed[i].GetProperty("static_id").GetInt32(),mappings[i].GetProperty("result_static_ids").EnumerateArray().Select(v=>v.GetInt32()));
            Assert.Contains(observed[i].GetProperty("type").GetInt32(),new[]{3,7});
            foreach(string key in new[]{"left","bottom","width","height"})
                Assert.InRange(observed[i].GetProperty("bounds_mm").GetProperty(key).GetDouble()-created.GetProperty("items")[i].GetProperty("bounds_mm").GetProperty(key).GetDouble(),-.001,.001);
        }
    }

    [LiveCorelFact]
    public async Task NativeDuplicatePreservesSourcesAndCopyMajorOrder()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var created=await client.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=Enumerable.Range(0,2).Select(i=>new {kind="rectangle",x_mm=i*20,y_mm=10,width_mm=6,height_mm=4}).ToArray()});
        var ids=created.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var result=await client.Draw(doc,"corel_duplicate_shapes",new {static_ids=ids,copies=10,step_x_mm=1,step_y_mm=5});
        Assert.Equal("vba",result.GetProperty("execution_engine").GetString());Assert.Equal(20,result.GetProperty("created_static_ids").GetArrayLength());
        var items=result.GetProperty("items").EnumerateArray().ToArray();
        for(int i=0;i<20;i++)
        {
            Assert.DoesNotContain(items[i].GetProperty("static_id").GetInt32(),ids);
            var b=items[i].GetProperty("bounds_mm");Assert.InRange(b.GetProperty("left").GetDouble(),(i%2)*20+i/2+1-.001,(i%2)*20+i/2+1+.001);
            Assert.InRange(b.GetProperty("bottom").GetDouble(),10+(i/2+1)*5-.001,10+(i/2+1)*5+.001);
        }
        Assert.Equal(22,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }

    [LiveCorelFact]
    public async Task CorelWideTextDropsLfAndPreservesCr()
    {
        using var client=new LiveDrawingTests.DrawingClient();var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var created=await client.Draw(doc,"corel_create_text",new {kind="artistic",layer_id=layer,text="A",x_mm=0,y_mm=0,font_family="Arial",font_size_pt=12});
        var ids=created.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        foreach(string text in new[]{"A\r\nB","A\nB","A\rB"})
        {
            var result=await client.Draw(doc,"corel_edit_text",new {static_ids=ids,action="set_text",text});
            Assert.Equal(text.Replace("\n",""),result.GetProperty("items")[0].GetProperty("text").GetString());
        }
    }

    [LiveCorelFact]
    public async Task TwentyTextEditsUseNativeAndVerifyOrdinalReplacementSetAndFormat()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var created=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {
            kind="artistic_text",item_key="text"+i,text="Ёж ёж ЁЖ "+i,x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12
        }).ToArray()});
        var ids=created.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var replaced=await client.Draw(doc,"corel_edit_text",new {static_ids=ids,action="replace_literal",old="ёж",@new="Лис",case_sensitive=false});
        Assert.Equal("vba",replaced.GetProperty("execution_engine").GetString());
        for(int i=0;i<20;i++)Assert.Equal("Лис Лис Лис "+i,replaced.GetProperty("items")[i].GetProperty("text").GetString());
        var formatted=await client.Draw(doc,"corel_edit_text",new {static_ids=ids,action="format",font_family="Arial",font_size_pt=15,bold=true,italic=true,alignment="center",line_spacing_percent=120});
        foreach(var item in formatted.GetProperty("items").EnumerateArray())
        {Assert.True(item.GetProperty("bold").GetBoolean());Assert.True(item.GetProperty("italic").GetBoolean());Assert.Equal(15,item.GetProperty("font_size_pt").GetDouble());}
        var set=await client.Draw(doc,"corel_edit_text",new {static_ids=ids,action="set_text",text="Строка 1\r\nСтрока 2"});
        foreach(var item in set.GetProperty("items").EnumerateArray())Assert.Equal("Строка 1\rСтрока 2",item.GetProperty("text").GetString());
    }

    [LiveCorelFact]
    public async Task ThousandColoredRotatedShapesHaveVerifiedCentersAndColors()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc=(await client.Ok("corel_create_document",new {request_id=Guid.NewGuid().ToString(),width_mm=760,height_mm=490})).GetProperty("document_id").GetString()!;
        var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        int[][] stops=[[255,0,0],[255,255,0],[0,255,0],[0,255,255],[0,0,255],[255,0,255]];
        int[] Rgb(int i)=>Enumerable.Range(0,3).Select(c=>(stops[i/200][c]*200+(stops[i/200+1][c]-stops[i/200][c])*(i%200)+100)/200).ToArray();
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var result=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,1000).Select(i=>new {
            kind="rectangle",item_key="c"+i,x_mm=20+(i%40)*18,y_mm=20+(i/40)*18,width_mm=12,height_mm=8,rotation_deg=i%360,
            fill=new {kind="uniform",color=new {model="rgb",r=Rgb(i)[0],g=Rgb(i)[1],b=Rgb(i)[2]}},outline=new {kind="none"}
        }).ToArray()});
        timer.Stop();var items=result.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(1000,items.Length);
        Assert.Equal(1000,result.GetProperty("created_static_ids").GetArrayLength());
        for(int i=0;i<1000;i++)
        {
            Assert.Equal("c"+i,items[i].GetProperty("item_key").GetString());
            var rgb=Rgb(i);var color=items[i].GetProperty("fill_color");Assert.Equal(rgb[0],color.GetProperty("r").GetInt32());Assert.Equal(rgb[1],color.GetProperty("g").GetInt32());Assert.Equal(rgb[2],color.GetProperty("b").GetInt32());
            var b=items[i].GetProperty("bounds_mm");Assert.InRange(b.GetProperty("left").GetDouble()+b.GetProperty("width").GetDouble()/2,26+(i%40)*18-.001,26+(i%40)*18+.001);
            Assert.InRange(b.GetProperty("bottom").GetDouble()+b.GetProperty("height").GetDouble()/2,24+(i/40)*18-.001,24+(i/40)*18+.001);
            Assert.InRange(items[i].GetProperty("rotation_deg").GetDouble(),i%360-.001,i%360+.001);Assert.Equal(0,items[i].GetProperty("outline_type").GetInt32());
        }
        await client.Ok("corel_save_document",new {document_id=doc,request_id=Guid.NewGuid().ToString(),path=Path.Combine(client.Directory,"hybrid-1000.cdr")});
        File.WriteAllText(Path.Combine(client.Directory,"native-timing.json"),System.Text.Json.JsonSerializer.Serialize(new {operation="create_batch",objects=1000,worker_operation_ms=timer.Elapsed.TotalMilliseconds,correctness="all-items-verified",not_full_scenario_comparison=true}));
    }

    [LiveCorelFact]
    public async Task TextBatchRejectsUnsupportedLastItemAndCreatesActualCyrillicText()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var before=await client.Ok("corel_status",new {});
        var bad=await client.DrawFailure(doc,"corel_create_batch",new {layer_id=layer,items=new[]{new {
            kind="artistic_text",item_key="bad",text="🦔",x_mm=0,y_mm=0,font_family="Arial",font_size_pt=12
        }}});
        Assert.False(bad.Error!.Applied);Assert.Equal(0,(await client.Inspect(doc)).GetProperty("total_shapes").GetInt32());
        var result=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {
            kind="artistic_text",item_key="t"+i,text="Ёж "+i,x_mm=i*5,y_mm=10,font_family="Arial",font_size_pt=12
        }).ToArray()});
        var items=result.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(20,items.Length);
        for(int i=0;i<20;i++){Assert.Equal("Ёж "+i,items[i].GetProperty("text").GetString());Assert.Equal("Arial",items[i].GetProperty("font_family").GetString());Assert.Equal(12,items[i].GetProperty("font_size_pt").GetDouble());}
        var after=await client.Ok("corel_status",new {});
        Assert.Equal(before.GetProperty("application_state").GetRawText(),after.GetProperty("application_state").GetRawText());
    }

    [LiveCorelFact]
    public async Task CreateAndUpdateBatchesReturnEveryVerifiedTarget()
    {
        using var client=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc=await client.Create();var initial=await client.Inspect(doc);
        var layer=initial.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
        var result=await client.Draw(doc,"corel_create_batch",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {
            kind="rectangle",item_key="r"+i,x_mm=i*8,y_mm=10,width_mm=6,height_mm=4,
            fill=new {kind="uniform",color=new {model="rgb",r=i,g=80,b=120}},outline=new {kind="none"}
        }).ToArray()});
        var items=result.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(20,items.Length);
        for(int i=0;i<items.Length;i++)
        {
            Assert.Equal("r"+i,items[i].GetProperty("item_key").GetString());
            Assert.Equal(i,items[i].GetProperty("fill_color").GetProperty("r").GetInt32());
            Assert.InRange(items[i].GetProperty("bounds_mm").GetProperty("left").GetDouble(),i*8-.001,i*8+.001);
        }
        var ids=result.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();Assert.Equal(20,ids.Length);
        var updated=await client.Draw(doc,"corel_update_batch",new {items=ids.Select(id=>new {
            static_id=id,transform=new {action="move",dx_mm=5,dy_mm=3},outline=new {kind="uniform",width_mm=.2,color=new {model="cmyk",c=0,m=0,y=0,k=100}}
        }).ToArray()});
        var after=updated.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(20,after.Length);
        for(int i=0;i<after.Length;i++)
        {
            Assert.Equal(ids[i],after[i].GetProperty("static_id").GetInt32());
            var bounds=after[i].GetProperty("bounds_mm");Assert.InRange(bounds.GetProperty("left").GetDouble(),i*8+4.999,i*8+5.001);
            Assert.InRange(bounds.GetProperty("bottom").GetDouble(),12.999,13.001);
            Assert.InRange(after[i].GetProperty("outline_width_mm").GetDouble(),.1999,.2001);
        }
        var independent=await client.Inspect(doc);Assert.Equal(20,independent.GetProperty("total_shapes").GetInt32());
        var inspected=independent.GetProperty("shapes").EnumerateArray().ToDictionary(s=>s.GetProperty("static_id").GetInt32());
        for(int i=0;i<20;i++)Assert.Equal(i,inspected[ids[i]].GetProperty("appearance").GetProperty("fill_color").GetProperty("r").GetInt32());
    }

    public void Dispose()=>UnloadTestRuntime();
    internal static void UnloadTestRuntime()
    {
        if(Environment.GetEnvironmentVariable("CORELDRAW_WORKER_LIVE")!="1")return;
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                using var lease=CorelMcp.Worker.Interop.ComOperationMutex.Acquire();
                using var connection=new CorelMcp.Worker.Interop.CorelConnection();connection.Connect("CorelDRAW.Application.27");
                using var scope=new CorelMcp.Worker.Interop.ComScope();
                var projects=scope.Track(scope.Track(connection.GetApplication().GMSManager).Projects);
                using var module=NativeModuleManifest.LoadAndVerify(Path.Combine(AppContext.BaseDirectory,"native"));
                for(int i=projects.Count;i>=1;i--)
                {
                    var p=scope.Track(projects[i]);
                    if(p.Name==module.ProjectName&&string.Equals(Path.GetFullPath(p.FullFileName),module.FullPath,StringComparison.OrdinalIgnoreCase))p.Unload();
                }
            }catch(Exception e){failure=e;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
