using System.Text.Json;
using Xunit;
using DrawingClient=CorelMcp.Worker.Tests.LiveDrawingTests.DrawingClient;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class PowerClipTests
{
    [LiveCorelFact]
    public async Task NestedPowerClipCanBePlacedAndExtractedOneLevelAtATime()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new object[]{new{kind="rectangle",x_mm=0,y_mm=0,width_mm=100,height_mm=100},new{kind="rectangle",x_mm=10,y_mm=10,width_mm=40,height_mm=40},new{kind="rectangle",x_mm=20,y_mm=20,width_mm=5,height_mm=7}}});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        await c.Draw(doc,"corel_powerclip",new{action="place",container_id=ids[1],static_ids=new[]{ids[2]}});
        await c.Draw(doc,"corel_powerclip",new{action="place",container_id=ids[0],static_ids=new[]{ids[1]}});
        await c.Draw(doc,"corel_powerclip",new{action="extract",container_id=ids[0]});
        var inner=await c.Draw(doc,"corel_powerclip",new{action="extract",container_id=ids[1]});
        Bounds(inner.GetProperty("contents")[0].GetProperty("bounds_mm"),20,20,5,7);
        Assert.Equal(3,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }
    [LiveCorelFact]
    public async Task CenteringKeepsRelativePlacementAndNeverScalesTheContents()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new object[]{new{kind="rectangle",x_mm=20,y_mm=20,width_mm=40,height_mm=40},new{kind="rectangle",x_mm=0,y_mm=0,width_mm=5,height_mm=10},new{kind="rectangle",x_mm=10,y_mm=0,width_mm=10,height_mm=10}}});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var placed=await c.Draw(doc,"corel_powerclip",new{action="place",container_id=ids[0],static_ids=ids.Skip(1).ToArray(),center=true});
        Bounds(placed.GetProperty("contents")[0].GetProperty("bounds_mm"),30,35,5,10);Bounds(placed.GetProperty("contents")[1].GetProperty("bounds_mm"),40,35,10,10);
    }
    [LiveCorelFact]
    public async Task PlaceAndExtractKeepContainerAndPreserveContentCoordinatesAndSize()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new object[]{new{kind="rectangle",x_mm=20,y_mm=20,width_mm=40,height_mm=40},new{kind="rectangle",x_mm=10,y_mm=10,width_mm=5,height_mm=7}}});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var placed=await c.Draw(doc,"corel_powerclip",new{action="place",container_id=ids[0],static_ids=new[]{ids[1]},center=false});
        var content=placed.GetProperty("contents")[0];Bounds(content.GetProperty("bounds_mm"),10,10,5,7);
        Assert.Equal(1,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
        var extracted=await c.Draw(doc,"corel_powerclip",new{action="extract",container_id=ids[0]});Assert.Equal(ids[0],extracted.GetProperty("container").GetProperty("static_id").GetInt32());
        Bounds(extracted.GetProperty("contents")[0].GetProperty("bounds_mm"),10,10,5,7);
        var final=await c.Inspect(doc);Assert.Equal(2,final.GetProperty("total_shapes").GetInt32());Assert.Contains(final.GetProperty("shapes").EnumerateArray(),v=>v.GetProperty("static_id").GetInt32()==ids[0]);
    }
    [LiveCorelFact]
    public async Task AncestorCycleIsRejectedBeforeMutation()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new[]{new{kind="rectangle",x_mm=0,y_mm=0,width_mm=20,height_mm=20},new{kind="rectangle",x_mm=30,y_mm=0,width_mm=10,height_mm=10}}});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();var group=await c.Draw(doc,"corel_group_shapes",new{action="group",static_ids=ids});
        var before=(await c.Inspect(doc)).GetRawText();var result=await c.DrawFailure(doc,"corel_powerclip",new{action="place",container_id=ids[0],static_ids=new[]{group.GetProperty("created_static_ids")[0].GetInt32()}});
        Assert.Equal("POWERCLIP_CYCLE",result.Error!.Code);Assert.False(result.Error.Applied);Assert.Equal(before,(await c.Inspect(doc)).GetRawText());
    }
    [LiveCorelFact]
    public async Task PagesUseOpaqueIdsAndResizingInvalidatesLayoutWithoutChangingOtherPage()
    {
        using var c=new DrawingClient();var doc=await c.Create();var before=await c.Inspect(doc);var first=before.GetProperty("page_id").GetString()!;
        var added=await c.Draw(doc,"corel_manage_pages",new{action="add",count=2,width_mm=100,height_mm=80});Assert.Equal(2,added.GetProperty("pages").GetArrayLength());
        var second=added.GetProperty("pages")[0].GetProperty("page_id").GetString()!;
        var mismatch=await c.DrawFailure(doc,"corel_manage_pages",new{action="resize",page_id=second,width_mm=150,height_mm=90});Assert.Equal("PAGE_MISMATCH",mismatch.Error!.Code);Assert.False(mismatch.Error.Applied);
        var layer=Layer(await c.Inspect(doc));var shape=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new[]{new{kind="rectangle",x_mm=10,y_mm=10,width_mm=20,height_mm=10}}});
        var ids=shape.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();var plan=await c.Draw(doc,"corel_layout_shapes",new{static_ids=ids});
        await c.Draw(doc,"corel_manage_pages",new{action="resize",page_id=first,width_mm=190,height_mm=140});
        var stale=await c.DrawFailure(doc,"corel_layout_shapes",new{static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});Assert.Equal("STALE_LAYOUT",stale.Error!.Code);Assert.False(stale.Error.Applied);
        var pages=(await c.Inspect(doc)).GetProperty("pages");Assert.Equal(100,pages[1].GetProperty("width").GetDouble());Assert.Equal(80,pages[1].GetProperty("height").GetDouble());Assert.Equal(3,pages.GetArrayLength());
    }
    private static string Layer(JsonElement inspection)=>inspection.GetProperty("layers").EnumerateArray().First(v=>!v.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    private static void Bounds(JsonElement b,double x,double y,double w,double h){foreach(var(key,expected)in new[]{("left",x),("bottom",y),("width",w),("height",h)})Assert.InRange(b.GetProperty(key).GetDouble(),expected-0.0002,expected+0.0002);}
}
