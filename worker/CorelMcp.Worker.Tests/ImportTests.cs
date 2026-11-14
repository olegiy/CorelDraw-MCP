using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class ImportTests
{
    [Fact]
    public void LongNativeImportPathIsRejectedExplicitlyBeforeCom()
    {
        var(source,run)=Fixture("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"10\"/></svg>");
        var deep=Path.Combine(run,new string('a',100),new string('b',100));Directory.CreateDirectory(deep);
        var error=Assert.Throws<OperationFailure>(()=>Prepare(source,deep));Assert.Equal("NATIVE_PATH_TOO_LONG",error.Code);Assert.False(error.Applied);
    }
    [Fact]
    public void SvgTextUsesTheSameUnicodeContractBeforeAnyNativeImport()
    {
        var(source,run)=Fixture("<svg xmlns=\"http://www.w3.org/2000/svg\"><text>Ёж 🦔</text></svg>");
        var error=Assert.Throws<OperationFailure>(()=>Prepare(source,run));Assert.Equal("TEXT_UNSUPPORTED_CHARACTER",error.Code);Assert.False(error.Applied);
    }
    [Fact]
    public void FrozenInputIsHashCheckedAndDoesNotFollowLaterSourceChanges()
    {
        var (source,run)=Fixture("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"20\"/></svg>");
        var prepared=Prepare(source,run);var path=prepared.GetProperty("path").GetString()!;
        Assert.NotEqual(source,path);Assert.StartsWith(Path.GetFullPath(run)+Path.DirectorySeparatorChar,path,StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(source,"changed");Assert.Contains("<rect",File.ReadAllText(path));Assert.False(string.IsNullOrEmpty(prepared.GetProperty("source_identity").GetString()));
    }
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/image.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"file:///C:/private.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import 'https://example.invalid/style.css';</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:url(https://example.invalid/a.svg#x)\"/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY data SYSTEM 'file:///C:/secret.txt'>]><svg xmlns=\"http://www.w3.org/2000/svg\">&data;</svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:u\\72l(https://example.invalid/a.svg)\"/></svg>")]
    public void SvgExternalReferencesEntitiesAndActiveContentNeverReachNativeFilter(string svg)
    {
        var (source,run)=Fixture(svg);Assert.Throws<OperationFailure>(()=>Prepare(source,run));
    }
    [Fact]
    public void InternalSvgPaintReferencesAreAllowedAndFingerprintMismatchIsExplicit()
    {
        var (source,run)=Fixture("<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\" width=\"10\" height=\"20\"/></svg>");
        Assert.True(File.Exists(Prepare(source,run).GetProperty("path").GetString()));
        var error=Assert.Throws<OperationFailure>(()=>Prepare(source,run,new string('0',64)));Assert.Equal("INPUT_FINGERPRINT_MISMATCH",error.Code);Assert.False(error.Applied);
    }
    [Fact]
    public void PdfSelectionProducesExactlyOnePageAndRejectsMissingPageAndCorruption()
    {
        var (source,run)=Fixture("","pdf");WritePdf(source);
        var result=Prepare(source,run,format:"pdf",page:2);var bytes=File.ReadAllText(result.GetProperty("path").GetString()!,Encoding.Latin1);
        Assert.Contains("/Count 1",bytes);Assert.Contains("/MediaBox",bytes);
        Assert.Equal("PDF_PAGE_NOT_FOUND",Assert.Throws<OperationFailure>(()=>Prepare(source,run,format:"pdf",page:3)).Code);
        File.WriteAllText(source,"not a pdf");Assert.Throws<OperationFailure>(()=>Prepare(source,run,format:"pdf",page:1));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncryptedPdfNeverPromptsOrPassesToNativeImport(bool userPassword)
    {
        var (source,run)=Fixture("","pdf");
        using(var pdf=new PdfSharp.Pdf.PdfDocument())
        {
            pdf.AddPage();if(userPassword)pdf.SecuritySettings.UserPassword="test-password";else pdf.SecuritySettings.OwnerPassword="owner-password";
            pdf.Save(source);
        }
        var failure=Assert.Throws<OperationFailure>(()=>Prepare(source,run,format:"pdf",page:1));Assert.Equal("PDF_PASSWORD_REQUIRED",failure.Code);Assert.False(failure.Applied);
        Assert.NotEmpty(Assert.IsType<string[]>(failure.Data["artifact_paths"]));
    }
    private static (string source,string run) Fixture(string content,string extension="svg")
    {
        var directory=Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../..")),"artifacts","worker-import-files",Guid.NewGuid().ToString("N"));
        var run=Path.Combine(directory,"run");Directory.CreateDirectory(run);var source=Path.Combine(directory,"input."+extension);File.WriteAllText(source,content,new UTF8Encoding(false));return(source,run);
    }
    private static JsonElement Prepare(string source,string run,string? sha=null,string format="svg",int? page=null)
    {
        var assembly=typeof(OperationDispatcher).Assembly;var type=assembly.GetType("CorelMcp.Worker.Operations.ImportSource");Assert.NotNull(type);
        var method=type.GetMethod("Prepare");Assert.NotNull(method);var command=assembly.GetType("CorelMcp.Worker.Operations.ImportCommand");Assert.NotNull(command);
        var args=new {path=source,format,sha256=sha??Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),size_bytes=new FileInfo(source).Length,layer_id="layer",x_mm=0,y_mm=0,pdf_page=page};
        try{return JsonSerializer.SerializeToElement(method.Invoke(null,[JsonSerializer.Deserialize(JsonSerializer.Serialize(args),command,Protocol.JsonOptions),run]),Protocol.JsonOptions);}
        catch(TargetInvocationException error){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw();throw;}
    }
    private static void WritePdf(string path)
    {
        string[] objects=["<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << >> >>"];
        var text=new StringBuilder("%PDF-1.4\n");List<int> offsets=[0];for(int i=0;i<objects.Length;i++){offsets.Add(text.Length);text.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
        int xref=text.Length;text.Append($"xref\n0 {objects.Length+1}\n0000000000 65535 f \n");foreach(int offset in offsets.Skip(1))text.Append(offset.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)+" 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");File.WriteAllText(path,text.ToString(),Encoding.ASCII);
    }
}
