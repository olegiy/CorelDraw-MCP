using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveHybridShowcaseTests
{
    [Fact]
    public void TenBlankPdfPagesDoNotPassTheVectorContentCheck()
    {
        string path=Path.Combine(Path.GetTempPath(),"CorelMcp_pdf_oracle_"+Guid.NewGuid().ToString("N")+".pdf");
        try
        {
            using(var pdf=new PdfDocument())
            {
                for(int i=0;i<10;i++){var page=pdf.AddPage();page.Width=PdfSharp.Drawing.XUnit.FromMillimeter(200);page.Height=PdfSharp.Drawing.XUnit.FromMillimeter(150);}
                pdf.Save(path);
            }
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(()=>ValidateVectorPdf(path));
        }
        finally {File.Delete(path);}
    }
    [LiveCorelFact]
    public async Task HundredLabelsAcrossTenPagesHaveVectorPdfContentAndPreserveSource()
    {
        using var c=new LiveDrawingTests.DrawingClient(ExecutionMode.Hybrid);
        var doc=await c.Create();
        await c.Draw(doc,"corel_manage_pages",new {action="add",count=9,width_mm=200,height_mm=150});
        var proofs=new List<string>();
        for(int page=1;page<=10;page++)
        {
            var state=await c.Ok("corel_inspect_document",new {document_id=doc,page_index=page,recursive=true,include_text=true});
            string layer=state.GetProperty("layers").EnumerateArray().First(l=>!l.GetProperty("is_special_layer").GetBoolean()).GetProperty("layer_id").GetString()!;
            var items=new List<object>();
            for(int i=0;i<10;i++)
            {
                double x=10+(i%2)*90,y=10+(i/2)*26;
                items.Add(new {kind="rectangle",item_key="cut"+i,x_mm=x,y_mm=y,width_mm=80,height_mm=22,
                    fill=new {kind="none"},outline=new {kind="uniform",width_mm=.1,color=new {model="rgb",r=255,g=0,b=255}}});
                items.Add(new {kind="artistic_text",item_key="label"+i,text=$"Этикетка {(page-1)*10+i+1:000}",x_mm=x+3,y_mm=y+8,font_family="Arial",font_size_pt=12});
            }
            await c.Ok("corel_create_batch",new {request_id=Guid.NewGuid().ToString(),document_id=doc,page_index=page,layer_id=layer,items});
            var after=await c.Ok("corel_inspect_document",new {document_id=doc,page_index=page,recursive=true,include_text=true});
            var shapes=after.GetProperty("shapes").EnumerateArray().ToArray();Assert.Equal(20,shapes.Length);
            for(int i=0;i<10;i++)Assert.Contains(shapes,s=>s.TryGetProperty("text",out var t)&&t.GetString()==$"Этикетка {(page-1)*10+i+1:000}");
            foreach(var shape in shapes.Where(s=>s.TryGetProperty("text",out var text)&&text.ValueKind==JsonValueKind.String))
            {
                var actual=await c.Ok("test_text_character",new {document_id=doc,page_index=page,static_id=shape.GetProperty("static_id").GetInt32(),index=1});
                Assert.Equal("Arial",actual.GetProperty("font").GetString());Assert.Equal(12,actual.GetProperty("size").GetDouble());
            }
            proofs.Add(after.GetRawText());
        }
        string cdr=Path.Combine(c.Directory,"100 этикеток — 10 страниц.cdr"),pdf=Path.Combine(c.Directory,"100 этикеток — 10 страниц.pdf");
        await c.Ok("corel_save_document",new {request_id=Guid.NewGuid().ToString(),document_id=doc,path=cdr});
        // Save changes Dirty; capture the comparison after saving and before exporting.
        proofs.Clear();
        for(int page=1;page<=10;page++)proofs.Add((await c.Ok("corel_inspect_document",new {document_id=doc,page_index=page,recursive=true,include_text=true})).GetRawText());
        await c.Ok("corel_export",new {request_id=Guid.NewGuid().ToString(),document_id=doc,format="pdf",range="document",path=pdf});
        ValidateVectorPdf(pdf);
        for(int page=1;page<=10;page++)Assert.Equal(proofs[page-1],(await c.Ok("corel_inspect_document",new {document_id=doc,page_index=page,recursive=true,include_text=true})).GetRawText());
        File.WriteAllText(Path.Combine(c.Directory,"showcase.json"),JsonSerializer.Serialize(new {cdr,pdf,pages=10,labels=100,cut_contours=100,pdf_content_checked=true,source_preserved=true}));
    }
    private static void ValidateVectorPdf(string pdf)
    {
        using(var exported=PdfReader.Open(pdf,PdfDocumentOpenMode.Import))
        {
            Assert.Equal(10,exported.PageCount);
            for(int page=0;page<10;page++)
            {
                var p=exported.Pages[page];Assert.InRange(p.Width.Millimeter,199.99,200.01);Assert.InRange(p.Height.Millimeter,149.99,150.01);
                var operators=ContentReader.ReadContent(p).OfType<COperator>().Select(o=>o.Name).ToList();
                CollectForms(p.Resources,operators,new HashSet<PdfDictionary>());
                Assert.True(operators.Count(n=>n is "S" or "s" or "B" or "B*" or "b" or "b*")>=10,$"Страница {page+1}: отсутствуют 10 векторных контуров резки: {string.Join(',',operators)}");
                Assert.Contains(operators,n=>n is "m" or "re");
            }
        }
    }
    private static void CollectForms(PdfDictionary resources,List<string> operators,HashSet<PdfDictionary> visited)
    {
        var objects=resources.Elements.GetDictionary("/XObject");if(objects is null)return;
        foreach(var name in objects.Elements.Keys)
        {
            var item=objects.Elements.GetDictionary(name);if(item is null||!visited.Add(item))continue;
            Assert.NotEqual("/Image",item.Elements.GetName("/Subtype"));
            if(item.Elements.GetName("/Subtype")!="/Form")continue;
            operators.AddRange(ContentReader.ReadContent(item.Stream.UnfilteredValue).OfType<COperator>().Select(o=>o.Name));
            var nested=item.Elements.GetDictionary("/Resources");if(nested is not null)CollectForms(nested,operators,visited);
        }
    }
}
