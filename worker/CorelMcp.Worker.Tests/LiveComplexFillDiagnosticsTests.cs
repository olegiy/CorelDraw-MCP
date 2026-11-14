using System.Text.Json;
using Xunit;
namespace CorelMcp.Worker.Tests;
[Collection("CorelLive")]
public sealed class LiveComplexFillDiagnosticsTests
{
    private static string Layer(JsonElement state)=>state.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    [LiveCorelFact]
    public async Task TextureProbeRecordsActualPresetIdentityOnlyInOwnedFixture()
    {
        using var c=new LiveDrawingTests.DrawingClient();var control=await c.Create();var before=await c.Inspect(control);var doc=await c.Create();string layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=10,y_mm=10,width_mm=30,height_mm=20}}});
        var probe=await c.Ok("test_texture_probe",new {document_id=doc,static_ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray()});
        Assert.Contains(probe.GetProperty("status").GetString(),new[]{"preset_confirmed","preset_unconfirmed","unsupported"});
        File.WriteAllText(Path.Combine(c.Directory,"texture-preset-probe.json"),probe.GetRawText());
        Assert.Equal(before.GetRawText(),(await c.Inspect(control)).GetRawText());
    }
    [LiveCorelFact]
    public async Task FullColorVectorPatternSvgReportsObservedEmbeddedRaster()
    {
        using var c=new LiveDrawingTests.DrawingClient();var tile=(await c.Ok("corel_create_document",new {request_id=Guid.NewGuid().ToString(),width_mm=25,height_mm=25})).GetProperty("document_id").GetString()!;
        string tileLayer=Layer(await c.Inspect(tile));var geometry=await c.Draw(tile,"corel_create_shapes",new {layer_id=tileLayer,items=Enumerable.Range(0,3).Select(i=>new {kind="rectangle",x_mm=i*7,y_mm=i*4,width_mm=10,height_mm=10}).ToArray()});
        var tileIds=geometry.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        for(int i=0;i<3;i++)await c.Draw(tile,"corel_style_shapes",new {static_ids=new[]{tileIds[i]},fill=new {kind="uniform",color=new {model="rgb",r=i==0?255:0,g=i==1?255:0,b=i==2?255:0}},outline=new {kind="none"}});
        string tilePath=Path.Combine(c.Directory,"own-vector-tile.cdr");await c.Ok("corel_save_document",new {request_id=Guid.NewGuid().ToString(),document_id=tile,path=tilePath});
        await c.Ok("corel_close_document",new {request_id=Guid.NewGuid().ToString(),document_id=tile,discard_changes=false});
        var doc=await c.Create();var layer=Layer(await c.Inspect(doc));var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=Enumerable.Range(0,20).Select(i=>new {kind="rectangle",x_mm=(i%5)*35,y_mm=(i/5)*30,width_mm=30,height_mm=25}).ToArray()});
        await c.Ok("test_pattern_fill",new {document_id=doc,path=tilePath,static_ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray()});
        var before=await c.Inspect(doc);string path=Path.Combine(c.Directory,"pattern.svg");
        var svg=await c.Ok("corel_export",new {request_id=Guid.NewGuid().ToString(),document_id=doc,format="svg",path});
        Assert.Equal("contains_raster_images",svg.GetProperty("fidelity").GetString());Assert.Contains("<image",File.ReadAllText(path));
        Assert.Equal(20,svg.GetProperty("linked_raster_files").GetArrayLength());
        foreach(var asset in svg.GetProperty("linked_raster_files").EnumerateArray())Assert.True(File.Exists(asset.GetString()));
        Assert.Equal(before.GetRawText(),(await c.Inspect(doc)).GetRawText());
    }
}
