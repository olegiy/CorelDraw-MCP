using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CorelMcp.Worker.Hybrid;

internal sealed record NativeBatchReply(int Protocol,string Nonce,string Operation,bool Ok,bool? Applied,bool Partial,
    int[] CreatedStaticIds,int[] AffectedStaticIds,int[] KnownBeforeStaticIds,JsonElement[] Items,string ErrorCode,string ErrorMessage);

internal static class NativeBatchResults
{
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true};
    public static NativeBatchReply Read(string raw,string nonce,string operation,string[] expectedKeys,int[] targets)
    {
        if(raw.Length>4*1024*1024||Encoding.UTF8.GetByteCount(raw)>4*1024*1024)throw Invalid();
        using var doc=JsonDocument.Parse(raw);UniqueKeys(doc.RootElement);
        var reply=doc.RootElement.Deserialize<NativeBatchReply>(Json)??throw Invalid();
        if(reply.Protocol!=1||reply.Nonce!=nonce||reply.Operation!=operation||reply.Items is null
            ||reply.CreatedStaticIds is null||reply.AffectedStaticIds is null||reply.KnownBeforeStaticIds is null||reply.ErrorCode is null||reply.ErrorMessage is null)throw Invalid();
        static bool Ids(int[] ids)=>ids.Length<=1000&&ids.All(i=>i>0)&&ids.Distinct().Count()==ids.Length;
        if(!Ids(reply.CreatedStaticIds)||!Ids(reply.AffectedStaticIds)||!Ids(reply.KnownBeforeStaticIds)||reply.Items.Length>expectedKeys.Length)throw Invalid();
        if(reply.KnownBeforeStaticIds.Any(id=>!targets.Contains(id))||reply.CreatedStaticIds.Intersect(reply.KnownBeforeStaticIds).Any())throw Invalid();
        if(operation=="create_batch"&&reply.KnownBeforeStaticIds.Length!=0)throw Invalid();
        if(operation=="update_batch"&&!reply.KnownBeforeStaticIds.Order().SequenceEqual(reply.AffectedStaticIds.Order()))throw Invalid();
        if(reply.CreatedStaticIds.Any(id=>!reply.AffectedStaticIds.Contains(id)))throw Invalid();
        if(operation=="update_batch"&&(reply.CreatedStaticIds.Length!=0||reply.AffectedStaticIds.Any(id=>!targets.Contains(id))))throw Invalid();
        bool conversion=operation=="convert_text";
        if(operation is not ("create_batch" or "update_batch" or "convert_text"))throw Invalid();
        if(reply.Ok&&(reply.Applied!=true||reply.Partial||reply.ErrorCode!=""||reply.ErrorMessage!=""
            ||reply.Items.Length!=expectedKeys.Length||!conversion&&reply.AffectedStaticIds.Length!=expectedKeys.Length
            ||operation=="create_batch"&&reply.CreatedStaticIds.Length!=expectedKeys.Length))throw Invalid();
        if(!reply.Ok&&(reply.ErrorCode.Length==0||reply.Applied==false&&(reply.CreatedStaticIds.Length!=0||reply.AffectedStaticIds.Length!=0||reply.KnownBeforeStaticIds.Length!=0||reply.Items.Length!=0)))throw Invalid();
        var seen=new HashSet<int>();
        for(int i=0;i<reply.Items.Length;i++)
        {
            var item=reply.Items[i];int id=item.GetProperty("static_id").GetInt32();
            if(item.GetProperty("type").GetInt32()<=0)throw Invalid();
            if(conversion)
            {
                var after=item.GetProperty("result_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
                if(item.GetProperty("source_static_id").GetInt32()!=targets[i]||!reply.KnownBeforeStaticIds.Contains(targets[i])||!Ids(after)||!after.Contains(id)
                    ||after.Any(value=>!reply.AffectedStaticIds.Contains(value)))throw Invalid();
            }
            if(!seen.Add(id)||!reply.AffectedStaticIds.Contains(id)||item.GetProperty("item_key").GetString()!=expectedKeys[i]
                ||operation=="create_batch"&&!reply.CreatedStaticIds.Contains(id)
                ||operation=="update_batch"&&id!=targets[i])throw Invalid();
            var bounds=item.GetProperty("bounds_mm");
            foreach(var property in new[]{"left","bottom","width","height"})
            {
                double n=bounds.GetProperty(property).GetDouble();
                if(!double.IsFinite(n)||property is "width" or "height"&&n<0)throw Invalid();
            }
            if(!double.IsFinite(item.GetProperty("rotation_deg").GetDouble())
                ||!double.IsFinite(item.GetProperty("outline_width_mm").GetDouble())
                ||item.GetProperty("outline_width_mm").GetDouble()<0)throw Invalid();
        }
        if(conversion&&reply.Ok)
        {
            var all=reply.Items.SelectMany(item=>item.GetProperty("result_static_ids").EnumerateArray().Select(v=>v.GetInt32())).ToArray();
            if(!reply.KnownBeforeStaticIds.Order().SequenceEqual(targets.Order())||all.Distinct().Count()!=all.Length||!all.Order().SequenceEqual(reply.AffectedStaticIds.Order())
                ||!all.Except(targets).Order().SequenceEqual(reply.CreatedStaticIds.Order()))throw Invalid();
        }
        return reply;
    }
    private static void UniqueKeys(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw Invalid();UniqueKeys(property.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())UniqueKeys(item);
    }
    private static InvalidDataException Invalid()=>new("Результат native batch не соответствует подтверждённому запросу; автоматический повтор запрещён.");
}
