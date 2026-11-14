using System.Xml;
namespace CorelMcp.Worker.Operations;
internal sealed record SvgFidelity(string Status,bool EmbeddedRaster,string[] LinkedRasterReferences);
internal static class ExportFidelity
{
    internal static string Svg(TextReader input)=>AnalyzeSvg(input).Status;
    internal static SvgFidelity AnalyzeSvg(TextReader input)
    {
        try
        {
            using var reader=XmlReader.Create(input,new XmlReaderSettings {DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=64*1024*1024,MaxCharactersFromEntities=0});
            bool embedded=false;var links=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(reader.Read())if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="image")
            {
                while(reader.MoveToNextAttribute())if(reader.LocalName=="href")
                {
                    if(System.Text.RegularExpressions.Regex.IsMatch(reader.Value,@"\Adata:image/(?:png|jpe?g|gif|bmp|tiff?|webp)[;,]",System.Text.RegularExpressions.RegexOptions.IgnoreCase))embedded=true;
                    else if(System.Text.RegularExpressions.Regex.IsMatch(reader.Value,@"\.(?:png|jpe?g|gif|bmp|tiff?|webp)\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {if(links.Count>=10000)throw new XmlException("Превышен бюджет SVG assets.");links.Add(reader.Value);}
                }
                reader.MoveToElement();
            }
            return new(embedded||links.Count>0?"contains_raster_images":"unknown",embedded,links.ToArray());
        }
        catch(XmlException){return new("unknown",false,[]);}
    }
}
