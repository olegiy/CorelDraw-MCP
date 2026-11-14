using CorelMcp.Worker.Operations;
using Xunit;
namespace CorelMcp.Worker.Tests;
public sealed class ExportFidelityTests
{
    [Fact] public void SvgRasterEvidenceIsSpecificAndAbsenceNeverPromisesVectorPreservation()
    {
        Assert.Equal("contains_raster_images",ExportFidelity.Svg(new StringReader("<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/png;base64,AAAA'/></svg>")));
        Assert.Equal("unknown",ExportFidelity.Svg(new StringReader("<svg><path d='M0 0L10 10'/></svg>")));
        Assert.Equal("unknown",ExportFidelity.Svg(new StringReader("<svg><image href='external.svg'/></svg>")));
        Assert.Equal("unknown",ExportFidelity.Svg(new StringReader("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///no-read'>]><svg>&x;</svg>")));
        var linked=ExportFidelity.AnalyzeSvg(new StringReader("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'><svg><image href='own_Images/img1.png'/></svg>"));
        Assert.Equal("contains_raster_images",linked.Status);Assert.False(linked.EmbeddedRaster);Assert.Equal("own_Images/img1.png",Assert.Single(linked.LinkedRasterReferences));
    }
}
