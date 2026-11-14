using System.Text;
using System.Text.Json;
using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

internal sealed record GeometryTargetMap(int SourceStaticId,VGCore.Shape PrivateShape);
internal static class NativeGeometry
{
    internal const int MaxBytes=64*1024*1024;
    public static string Extract(OperationContext c,VGCore.Document privateDocument,IReadOnlyList<GeometryTargetMap> targets,double mm)
    {
        if(c.Target is not null&&ComScope.Identity(privateDocument)==ComScope.Identity(c.Target.Document))
            throw new OperationFailure("GEOMETRY_SOURCE_GUARD","Нативное извлечение разрешено только в собственном private document.");
        var runtime=c.Session.Native;runtime.EnsureReady(c);runtime.RequireOperation("extract_geometry");
        using var scope=c.Session.CreateScope();var range=scope.Track(c.App.CreateShapeRange());
        foreach(var target in targets)range.Add(target.PrivateShape);
        var ids=targets.Select(t=>t.SourceStaticId).ToArray();string nonce=Guid.NewGuid().ToString("N");
        string raw=runtime.ExtractGeometry(c,privateDocument,range,ids,mm,nonce);
        Validate(raw,nonce,ids);
        string part=Path.Combine(Path.GetFullPath(c.Request.RunDirectory),"geometry-"+Guid.NewGuid().ToString("N")+".part");
        string output=Path.ChangeExtension(part,".json");bool owned=false;
        try
        {
            using(var file=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                owned=true;using var document=JsonDocument.Parse(raw);using(var writer=new Utf8JsonWriter(file))
                {writer.WriteStartObject();writer.WritePropertyName("objects");document.RootElement.GetProperty("objects").WriteTo(writer);writer.WriteEndObject();writer.Flush();}
                file.Flush(true);
            }
            File.Move(part,output);return output;
        }
        finally {if(owned&&File.Exists(part))c.Cleanup.Try("geometry.native_partial",()=>File.Delete(part));}
    }
    internal static void Validate(string raw,string nonce,int[] ids)
    {
        if(raw.Length>MaxBytes||Encoding.UTF8.GetByteCount(raw)>MaxBytes)throw Invalid();
        using var document=JsonDocument.Parse(raw);var root=document.RootElement;
        Keys(root,"protocol","nonce","objects");
        if(root.GetProperty("protocol").GetInt32()!=1||root.GetProperty("nonce").GetString()!=nonce)throw Invalid();
        var objects=root.GetProperty("objects").EnumerateArray().ToArray();if(objects.Length!=ids.Length)throw Invalid();
        int count=0;
        for(int i=0;i<objects.Length;i++)
        {
            var obj=objects[i];Keys(obj,"static_id","segments");if(obj.GetProperty("static_id").GetInt32()!=ids[i])throw Invalid();
            int before=count;
            foreach(var segment in obj.GetProperty("segments").EnumerateArray())
            {
                if(++count>200000)throw Invalid();
                int points=segment.GetProperty("kind").GetString() switch {"line"=>2,"cubic"=>4,_=>throw Invalid()};
                Keys(segment,new[]{"kind"}.Concat(Enumerable.Range(0,points).Select(n=>"p"+n)).ToArray());
                for(int p=0;p<points;p++)
                {
                    var point=segment.GetProperty("p"+p);Keys(point,"x","y");
                    if(!double.IsFinite(point.GetProperty("x").GetDouble())||!double.IsFinite(point.GetProperty("y").GetDouble()))throw Invalid();
                }
            }
            if(count==before)throw Invalid();
        }
    }
    private static void Keys(JsonElement element,params string[] expected)
    {
        var actual=element.EnumerateObject().Select(p=>p.Name).ToArray();
        if(actual.Length!=expected.Length||actual.Distinct(StringComparer.Ordinal).Count()!=actual.Length||actual.Any(n=>!expected.Contains(n,StringComparer.Ordinal)))throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("Нативный снимок геометрии не прошёл проверку структуры, идентичности или лимитов.");
}
