using System.Text.Json;
using Xunit;
using DrawingClient = CorelMcp.Worker.Tests.LiveDrawingTests.DrawingClient;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveLayoutTests
{
    [LiveCorelFact]
    public async Task NonprintingLayoutHasVerifiedBoundsOrExplicitRefusalAndNeverTogglesSourceLayer()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=10,y_mm=10,width_mm=30,height_mm=20}}});
        int id=made.GetProperty("created_static_ids")[0].GetInt32();
        await c.Draw(doc,"corel_style_shapes",new {static_ids=new[]{id},outline=new {kind="none"}});
        await c.Draw(doc,"corel_manage_layers",new {action="set_flags",layer_id=layer,printable=false});
        var before=(await c.Inspect(doc)).GetRawText();
        var result=await c.Call("corel_layout_shapes",new {request_id=Guid.NewGuid().ToString(),document_id=doc,page_index=1,static_ids=new[]{id}});
        if(result.Ok)
        {
            var details=JsonSerializer.SerializeToElement(result.Result,Protocol.JsonOptions);
            Assert.Equal(30,details.GetProperty("placements")[0].GetProperty("width_mm").GetDouble(),3);
            Assert.Equal(20,details.GetProperty("placements")[0].GetProperty("height_mm").GetDouble(),3);
        }
        else {Assert.Equal("UNSUPPORTED_LAYOUT_BOUNDS",result.Error!.Code);Assert.False(result.Error.Applied);}
        Assert.Equal(before,(await c.Inspect(doc)).GetRawText());
    }

    [LiveCorelFact]
    public async Task FullRevisionCanReadTextCurvesAndGroupsWithoutChangingTheSource()
    {
        foreach(var unit in new[]{"mm","inch"})
        {
            using var c=new DrawingClient(); var doc=await c.Create(); var layer=Layer(await c.Inspect(doc));
            await c.Ok("test_configure",new {document_id=doc,unit});
            var shapes=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new object[]{
                new {kind="rectangle",x_mm=10,y_mm=10,width_mm=20,height_mm=10},new {kind="ellipse",x_mm=40,y_mm=10,width_mm=10,height_mm=10}}});
            var text=await c.Draw(doc,"corel_create_text",new {kind="artistic",layer_id=layer,text="Ёж",x_mm=10,y_mm=50,font_family="Arial",font_size_pt=12});
            int[] children=shapes.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
            var grouped=await c.Draw(doc,"corel_group_shapes",new {action="group",static_ids=children});
            int[] ids=[grouped.GetProperty("created_static_ids")[0].GetInt32(),text.GetProperty("created_static_ids")[0].GetInt32()];
            var before=(await c.Inspect(doc)).GetRawText();
            var plan=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids,margin_left_mm=5,margin_bottom_mm=5,gap_x_mm=2});
            Assert.Empty(plan.GetProperty("unplaced").EnumerateArray()); Assert.Equal(before,(await c.Inspect(doc)).GetRawText());
            var applied=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids,margin_left_mm=5,margin_bottom_mm=5,gap_x_mm=2,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});
            Assert.Equal(2,applied.GetProperty("actual_bounds").GetArrayLength());
        }
    }
    [LiveCorelFact]
    public async Task LayoutDryRunPreservesStateAndApplyChecksRevisionAndOutlineBounds()
    {
        using var c = new DrawingClient(); var doc = await c.Create();
        var layer = Layer(await c.Inspect(doc));
        var shapes = await c.Draw(doc,"corel_create_shapes",new { layer_id=layer,items=new object[]{
            new {kind="rectangle",x_mm=10,y_mm=10,width_mm=30,height_mm=20},
            new {kind="rectangle",x_mm=70,y_mm=20,width_mm=20,height_mm=10}}});
        int[] ids=shapes.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        await c.Draw(doc,"corel_style_shapes",new {static_ids=ids,outline=new {kind="uniform",width_mm=2,color=new {model="rgb",r=0,g=0,b=0}}});
        var control=await c.Create(); var controlBefore=(await c.Inspect(control)).GetRawText();
        var before=(await c.Inspect(doc)).GetRawText();
        var plan=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids,margin_left_mm=5,margin_right_mm=5,margin_bottom_mm=5,margin_top_mm=5,gap_x_mm=3,gap_y_mm=4});
        Assert.Equal(before,(await c.Inspect(doc)).GetRawText()); Assert.Empty(plan.GetProperty("unplaced").EnumerateArray());
        Position(plan.GetProperty("placements")[0],5,5,32,22); Position(plan.GetProperty("placements")[1],40,5,22,12);
        var applied=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString(),margin_left_mm=5,margin_right_mm=5,margin_bottom_mm=5,margin_top_mm=5,gap_x_mm=3,gap_y_mm=4});
        Position(applied.GetProperty("actual_bounds")[0],5,5,32,22); Position(applied.GetProperty("actual_bounds")[1],40,5,22,12);
        plan=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids});
        await c.Draw(doc,"corel_transform_shapes",new {static_ids=new[]{ids[0]},action="rotate",angle_deg=180,anchor_x_mm=21.0,anchor_y_mm=16.0});
        var stale=await c.DrawFailure(doc,"corel_layout_shapes",new {static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});
        Assert.Equal("STALE_LAYOUT",stale.Error!.Code); Assert.False(stale.Error.Applied);
        Assert.Equal(controlBefore,(await c.Inspect(control)).GetRawText());
    }
    [LiveCorelFact]
    public async Task GridCountsCopiesAndReportsPartialCreatedIdsWithoutReplay()
    {
        using var c=new DrawingClient(); var doc=await c.Create(); var layer=Layer(await c.Inspect(doc));
        var created=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=0,y_mm=0,width_mm=10,height_mm=10}}});
        int id=created.GetProperty("created_static_ids")[0].GetInt32();
        await c.Draw(doc,"corel_style_shapes",new {static_ids=new[]{id},outline=new {kind="uniform",width_mm=2,color=new {model="rgb",r=0,g=0,b=0}}});
        var grid=await c.Draw(doc,"corel_grid_duplicate",new {static_ids=new[]{id},rows=2,columns=3,gap_x_mm=3,gap_y_mm=4,anchor_x_mm=10,anchor_y_mm=20});
        Assert.Equal(5,grid.GetProperty("created_static_ids").GetArrayLength()); Assert.Equal(6,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
        Position(grid.GetProperty("actual_bounds")[0],10,20,12,12); Position(grid.GetProperty("actual_bounds")[1],25,20,12,12); Position(grid.GetProperty("actual_bounds")[3],10,36,12,12);
        c.FailAfterMutations=3;
        var partial=await c.DrawFailure(doc,"corel_grid_duplicate",new {static_ids=new[]{id},rows=1,columns=4,gap_x_mm=1,anchor_x_mm=0,anchor_y_mm=0});
        Assert.True(partial.Error!.Applied); var details=JsonSerializer.SerializeToElement(partial.Result,Protocol.JsonOptions);
        Assert.Equal(2,details.GetProperty("created_static_ids").GetArrayLength()); Assert.Equal(8,(await c.Inspect(doc)).GetProperty("total_shapes").GetInt32());
    }
    [LiveCorelFact]
    public async Task UnplacedLayoutCannotApplyAndAlignUsesBoundsWithoutOutline()
    {
        using var c=new DrawingClient(); var doc=await c.Create(); var layer=Layer(await c.Inspect(doc));
        var made=await c.Draw(doc,"corel_create_shapes",new {layer_id=layer,items=new[]{new {kind="rectangle",x_mm=0,y_mm=0,width_mm=300,height_mm=10}}});
        var ids=made.GetProperty("created_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        var plan=await c.Draw(doc,"corel_layout_shapes",new {static_ids=ids}); Assert.Single(plan.GetProperty("unplaced").EnumerateArray());
        var before=(await c.Inspect(doc)).GetRawText();
        var result=await c.DrawFailure(doc,"corel_layout_shapes",new {static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});
        Assert.Equal("UNPLACED_ITEMS",result.Error!.Code); Assert.False(result.Error.Applied); Assert.Equal(before,(await c.Inspect(doc)).GetRawText());
        await c.Draw(doc,"corel_style_shapes",new {static_ids=ids,outline=new {kind="uniform",width_mm=10,color=new {model="rgb",r=0,g=0,b=0}}});
        var aligned=await c.Draw(doc,"corel_align_shapes",new {static_ids=ids,axis="x",action="align",align="center",reference="page"});
        var bounds=aligned.GetProperty("items")[0].GetProperty("bounds_mm"); Assert.InRange(bounds.GetProperty("left").GetDouble(),-50.0001,-49.9999);
    }
    private static string Layer(JsonElement value)=>value.GetProperty("layers").EnumerateArray().First(v=>!v.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    private static void Position(JsonElement p,double x,double y,double w,double h)
    {
        foreach(var (name,expected) in new[]{("x_mm",x),("y_mm",y),("width_mm",w),("height_mm",h)}) Assert.InRange(p.GetProperty(name).GetDouble(),expected-0.0002,expected+0.0002);
    }
}
