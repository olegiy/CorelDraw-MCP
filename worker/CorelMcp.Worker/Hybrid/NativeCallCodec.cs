using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CorelMcp.Worker.Hybrid;

internal sealed record NativeHandshake(int Protocol, string ModuleVersion, string SourceSha256, string Nonce, int CorelMajor, string[] Operations);
internal static class NativeCallCodec
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true };
    public static NativeHandshake ReadHandshake(string raw, string nonce, NativeModuleDescription module)
    {
        if(raw.Length>65536 || Encoding.UTF8.GetByteCount(raw)>65536) throw Invalid();
        using var doc=JsonDocument.Parse(raw);
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in doc.RootElement.EnumerateObject()) if(!keys.Add(p.Name)) throw Invalid();
        var value=doc.RootElement.Deserialize<NativeHandshake>(Json)??throw Invalid();
        if(value.Protocol!=module.Protocol || value.ModuleVersion!=module.ModuleVersion || value.SourceSha256!=module.SourceSha256
            || value.CorelMajor!=module.CorelMajor || value.Nonce!=nonce || value.Operations is null
            || !value.Operations.SequenceEqual(module.Operations,StringComparer.Ordinal)) throw Invalid();
        return value;
    }
    private static InvalidDataException Invalid()=>new("VBA runtime не подтвердил идентичность, nonce или контракт; выполнение запрещено.");
}
