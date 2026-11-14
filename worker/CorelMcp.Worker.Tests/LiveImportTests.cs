using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using PdfSharp.Drawing;
using Xunit;
using DrawingClient=CorelMcp.Worker.Tests.LiveDrawingTests.DrawingClient;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveImportTests
{
    [LiveCorelFact]
    public async Task ImportedBitmapsCanBuildMixedLayoutsWithoutWeakeningVectorOnlyOperations()
    {
        foreach(var unit in new[]{"mm","inch"})
        {
            using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
            await c.Ok("test_configure",new{document_id=doc,unit});
            var control=await c.Create();var controlBefore=(await c.Inspect(control)).GetRawText();
            var path=Path.Combine(c.Directory,"bitmap.png");
            using(var image=new Bitmap(20,10)){using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.Blue);image.Save(path,ImageFormat.Png);}
            var imported=await c.Draw(doc,"corel_import",Args(path,"png",layer,10,10));
            int bitmap=imported.GetProperty("created_static_ids")[0].GetInt32();
            await c.Draw(doc,"corel_transform_shapes",new{static_ids=new[]{bitmap},action="move",dx_mm=3,dy_mm=4});
            await c.Draw(doc,"corel_transform_shapes",new{static_ids=new[]{bitmap},action="scale",sx=1.5,sy=1.5,anchor_x_mm=0,anchor_y_mm=0});
            var duplicate=await c.Draw(doc,"corel_duplicate_shapes",new{static_ids=new[]{bitmap},copies=1,step_x_mm=30,step_y_mm=0});
            Assert.Single(duplicate.GetProperty("created_static_ids").EnumerateArray());
            var vector=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new[]{new{kind="rectangle",x_mm=5,y_mm=5,width_mm=5,height_mm=5}}});
            int rectangle=vector.GetProperty("created_static_ids")[0].GetInt32();
            var group=await c.Draw(doc,"corel_group_shapes",new{action="group",static_ids=new[]{bitmap,rectangle}});
            int groupId=group.GetProperty("created_static_ids")[0].GetInt32();
            var page=(await c.Inspect(doc)).GetProperty("pages")[0].GetProperty("page_id").GetString();
            var other=await c.Draw(doc,"corel_manage_layers",new{action="create",page_id=page,name="Bitmap assembly"});
            var otherId=other.GetProperty("layer").GetProperty("layer_id").GetString();
            await c.Draw(doc,"corel_manage_layers",new{action="move_shapes",layer_id=otherId,static_ids=new[]{groupId}});
            var grid=await c.Draw(doc,"corel_grid_duplicate",new{static_ids=new[]{groupId},rows=1,columns=2,gap_x_mm=2,anchor_x_mm=0,anchor_y_mm=0});
            int copy=grid.GetProperty("actual_bounds")[1].GetProperty("static_id").GetInt32();int[] ids=[groupId,copy];
            var before=(await c.Inspect(doc)).GetRawText();var plan=await c.Draw(doc,"corel_layout_shapes",new{static_ids=ids});
            Assert.Equal(before,(await c.Inspect(doc)).GetRawText());
            await c.Draw(doc,"corel_transform_shapes",new{static_ids=new[]{bitmap},action="move",dx_mm=1,dy_mm=0});
            var stale=await c.DrawFailure(doc,"corel_layout_shapes",new{static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});
            Assert.Equal("STALE_LAYOUT",stale.Error!.Code);Assert.False(stale.Error.Applied);
            plan=await c.Draw(doc,"corel_layout_shapes",new{static_ids=ids});
            await c.Draw(doc,"corel_layout_shapes",new{static_ids=ids,dry_run=false,expected_revision=plan.GetProperty("layout_revision").GetString()});
            await c.Draw(doc,"corel_group_shapes",new{action="ungroup",static_ids=new[]{groupId}});
            var rejected=await c.DrawFailure(doc,"corel_convert_to_curves",new{static_ids=new[]{bitmap}});
            Assert.Equal("UNSUPPORTED_SHAPE",rejected.Error!.Code);Assert.False(rejected.Error.Applied);
            rejected=await c.DrawFailure(doc,"corel_style_shapes",new{static_ids=new[]{bitmap},outline=new{kind="none"}});
            Assert.Equal("UNSUPPORTED_SHAPE",rejected.Error!.Code);Assert.False(rejected.Error.Applied);
            Assert.Equal(controlBefore,(await c.Inspect(control)).GetRawText());
        }
    }
    [LiveCorelFact]
    public async Task ImportReportsObservedFontsAndExplicitUnknownSourceFidelity()
    {
        for(int sample=0;sample<3;sample++)
        {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        var path=Path.Combine(c.Directory,"text-effect.svg");
        File.WriteAllText(path,"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100mm\" height=\"50mm\" viewBox=\"0 0 100 50\"><defs><filter id=\"blur\"><feGaussianBlur stdDeviation=\"1\"/></filter></defs><text x=\"10\" y=\"25\" font-family=\"Arial\" font-size=\"12\" filter=\"url(#blur)\">Привет</text></svg>");
        var imported=await c.Draw(doc,"corel_import",Args(path,"svg",layer,0,0));
        var diagnostics=imported.GetProperty("import_diagnostics");
        Assert.Equal("unknown",diagnostics.GetProperty("source_fidelity").GetString());
        Assert.Equal("unknown",diagnostics.GetProperty("font_fidelity").GetString());
        Assert.Equal("unknown",diagnostics.GetProperty("effect_fidelity").GetString());
        Assert.True(diagnostics.GetProperty("text_shape_count").GetInt32()>0);
        Assert.NotEmpty(diagnostics.GetProperty("observed_fonts").EnumerateArray());
        Assert.NotEmpty(diagnostics.GetProperty("warnings").EnumerateArray());
        Assert.Equal("Arial",diagnostics.GetProperty("observed_fonts")[0].GetProperty("family").GetString());
        Assert.Equal("windows_font_catalog",diagnostics.GetProperty("observed_fonts")[0].GetProperty("availability_basis").GetString());
        await c.Ok("corel_status",new{});
        }
    }
    [LiveCorelFact]
    public async Task NativeImportsUseFrozenLocalFilesOnePdfPageAndOwnedCopiesOnly()
    {
        foreach(var unit in new[]{"mm","inch"})
        {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));var control=await c.Create();var controlBefore=(await c.Inspect(control)).GetRawText();
        await c.Ok("test_configure",new{document_id=doc,unit});
        var directory=Path.Combine(c.Directory,"fixtures");Directory.CreateDirectory(directory);
        var svg=Path.Combine(directory,"Кириллица с пробелами.svg");File.WriteAllText(svg,"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40mm\" height=\"20mm\" viewBox=\"0 0 40 20\"><rect width=\"40\" height=\"20\" fill=\"#ff0000\"/><rect x=\"20\" width=\"20\" height=\"20\" fill=\"#0000ff\"/></svg>");
        var png=Path.Combine(directory,"input.png");var jpeg=Path.Combine(directory,"input.jpg");
        using(var image=new Bitmap(20,10)){using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.Red);image.Save(png,ImageFormat.Png);image.Save(jpeg,ImageFormat.Jpeg);}
        var pdf=Path.Combine(directory,"two-pages.pdf");
        using(var document=new PdfSharp.Pdf.PdfDocument())for(int page=0;page<2;page++)
        {
            var p=document.AddPage();p.Width=XUnit.FromPoint(page==0?100:200);p.Height=XUnit.FromPoint(100);
            using(var graphics=XGraphics.FromPdfPage(p))graphics.DrawRectangle(XBrushes.Red,10,10,page==0?40:80,40);
            if(page==1)document.Save(pdf);
        }
        int index=0;int? bitmapId=null;
        foreach(var (path,format) in new[]{(svg,"svg"),(png,"png"),(jpeg,"jpeg"),(pdf,"pdf")})
        {
            var fields=Args(path,format,layer,index*30+5,20);if(format=="pdf")fields["pdf_page"]=2;
            var imported=await c.Draw(doc,"corel_import",fields);
            var bounds=imported.GetProperty("import_bounds_mm");Bounds(bounds,index*30+5,20,20,10);
            Assert.NotEmpty(imported.GetProperty("created_static_ids").EnumerateArray());
            if(format=="png")bitmapId=imported.GetProperty("created_static_ids")[0].GetInt32();index++;
        }
        var preview=await c.Ok("corel_preview",new{document_id=doc,max_size=600});
        using(var image=new Bitmap(preview.GetProperty("path").GetString()!))
        {
            var pixel=image.GetPixel((int)(image.Width*20.0/200),(int)(image.Height*(1-25.0/150)));
            Assert.True(pixel.B>180&&pixel.R<100,$"SVG z-order: {pixel}");
        }
        var before=(await c.Inspect(doc)).GetRawText();File.WriteAllText(svg,"<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/image.png\"/></svg>");
        var rejected=await c.DrawFailure(doc,"corel_import",Args(svg,"svg",layer,0,0));Assert.Equal("SVG_EXTERNAL_REFERENCE",rejected.Error!.Code);Assert.False(rejected.Error.Applied);Assert.NotEmpty(rejected.Artifacts!);
        Assert.Equal(before,(await c.Inspect(doc)).GetRawText());Assert.Equal(controlBefore,(await c.Inspect(control)).GetRawText());
        var container=await c.Draw(doc,"corel_create_shapes",new{layer_id=layer,items=new[]{new{kind="rectangle",x_mm=0,y_mm=0,width_mm=100,height_mm=100}}});
        await c.Draw(doc,"corel_powerclip",new{action="place",container_id=container.GetProperty("created_static_ids")[0].GetInt32(),static_ids=new[]{bitmapId!.Value},center=false});
        Assert.Equal(2,(await c.Ok("corel_status",new{})).GetProperty("documents").GetArrayLength());
        }
    }
    [LiveCorelFact]
    public async Task ImportPreparationDocumentClosesAfterUndoStartsButDrawingBodyFails()
    {
        using var c=new DrawingClient();var doc=await c.Create();var layer=Layer(await c.Inspect(doc));
        await c.Ok("corel_save_document",new{request_id=Guid.NewGuid().ToString(),document_id=doc,path=Path.Combine(c.Directory,"before.cdr")});
        var control=await c.Create();var before=(await c.Inspect(doc)).GetRawText();
        var path=Path.Combine(c.Directory,"valid.svg");File.WriteAllText(path,"<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"20\" height=\"10\"/></svg>");
        c.FailBeforeDrawing=true;var rejected=await c.DrawFailure(doc,"corel_import",Args(path,"svg",layer,0,0));
        Assert.True(rejected.Error!.Applied);Assert.Equal(true,rejected.Cleanup!["completed"]);
        var expected=System.Text.Json.Nodes.JsonNode.Parse(before)!;expected["document"]!["dirty"]=true;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected,System.Text.Json.Nodes.JsonNode.Parse((await c.Inspect(doc)).GetRawText())));
        var status=await c.Ok("corel_status",new{});Assert.Equal(2,status.GetProperty("documents").GetArrayLength());Assert.Equal(control,status.GetProperty("active_document_id").GetString());
    }
    private static Dictionary<string,object?> Args(string path,string format,string layer,double x,double y)=>new()
    { ["path"]=path,["format"]=format,["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),["size_bytes"]=new FileInfo(path).Length,["layer_id"]=layer,["x_mm"]=x,["y_mm"]=y,["fit"]=new{width_mm=20,height_mm=20} };
    private static string Layer(JsonElement inspection)=>inspection.GetProperty("layers").EnumerateArray().First(v=>!v.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
    private static void Bounds(JsonElement b,double x,double y,double w,double h){foreach(var(key,expected)in new[]{("left",x),("bottom",y),("width",w),("height",h)})Assert.InRange(b.GetProperty(key).GetDouble(),expected-0.001,expected+0.001);}
}
