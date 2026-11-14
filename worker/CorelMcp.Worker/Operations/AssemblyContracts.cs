using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static CorelMcp.Worker.Operations.DrawingContracts;

namespace CorelMcp.Worker.Operations;

internal sealed record FitFrame(double WidthMm,double HeightMm);
internal sealed record ImportCommand(string Path,string Format,string Sha256,long SizeBytes,string LayerId,double XMm,double YMm,FitFrame? Fit=null,int? PdfPage=null):IDrawingCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(AddPagesCommand),"add"),JsonDerivedType(typeof(ResizePageCommand),"resize")]
internal abstract record PageCommand:IDrawingCommand;
internal sealed record AddPagesCommand(int Count,double WidthMm,double HeightMm):PageCommand;
internal sealed record ResizePageCommand(string PageId,double WidthMm,double HeightMm):PageCommand;
[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(PlaceClipCommand),"place"),JsonDerivedType(typeof(ExtractClipCommand),"extract")]
internal abstract record ClipCommand(int ContainerId):IDrawingCommand;
internal sealed record PlaceClipCommand(int ContainerId,int[] StaticIds,bool Center=false):ClipCommand(ContainerId);
internal sealed record ExtractClipCommand(int ContainerId):ClipCommand(ContainerId);

internal static class AssemblyContracts
{
    public static void Validate(IDrawingCommand command)
    {
        switch(command)
        {
            case ImportCommand import:
                OperationContracts.AbsolutePath(import.Path);Check(Regex.IsMatch(import.Path,"^[A-Za-z]:[\\\\/]",RegexOptions.CultureInvariant));
                Check(import.Format is "svg" or "pdf" or "png" or "jpeg");Check(Regex.IsMatch(import.Sha256,"^[a-f0-9]{64}$",RegexOptions.CultureInvariant));
                Check(import.SizeBytes is >0 and <=256L*1024*1024);Id(import.LayerId);Coord(import.XMm,import.YMm);
                Check(import.Format=="pdf"?import.PdfPage is >=1 and <=100000:import.PdfPage is null);
                if(import.Fit is not null){OperationContracts.Dimension(import.Fit.WidthMm);OperationContracts.Dimension(import.Fit.HeightMm);}break;
            case AddPagesCommand add:OperationContracts.Range(add.Count,1,100,"count");OperationContracts.Dimension(add.WidthMm);OperationContracts.Dimension(add.HeightMm);break;
            case ResizePageCommand resize:Id(resize.PageId);OperationContracts.Dimension(resize.WidthMm);OperationContracts.Dimension(resize.HeightMm);break;
            case ClipCommand clip:
                Check(clip.ContainerId>0);if(clip is PlaceClipCommand place){Ids(place.StaticIds);Check(!place.StaticIds.Contains(place.ContainerId));}break;
        }
    }
}
