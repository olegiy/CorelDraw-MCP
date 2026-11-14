using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CorelMcp.Worker.Hybrid;

internal sealed record NativeModuleDescription(int SchemaVersion, int Protocol, string ModuleVersion,
    int CorelMajor, string SourceSha256, string ProjectName, string File, string GmsSha256, string[] Operations);

internal sealed class VerifiedNativeModule : IDisposable
{
    private readonly FileStream manifestLease, moduleLease;
    public NativeModuleDescription Description { get; }
    public string FullPath { get; }
    public string ProjectName => Description.ProjectName;
    public int Protocol => Description.Protocol;
    internal VerifiedNativeModule(NativeModuleDescription description, string path, FileStream manifest, FileStream module)
    { Description = description; FullPath = path; manifestLease = manifest; moduleLease = module; }
    public void Dispose() { moduleLease.Dispose(); manifestLease.Dispose(); }
}

internal static class NativeModuleManifest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private static readonly HashSet<string> AllowedOperations = ["handshake", "validate_fonts", "create_batch", "update_batch",
        "create_shapes", "style", "move", "duplicate", "replace_literal", "set_text", "format_text", "convert_text", "extract_geometry"];

    public static VerifiedNativeModule LoadAndVerify(string directory)
    {
        string root = Path.GetFullPath(directory);
        NoLinks(root);
        FileStream? manifest = null, module = null;
        try
        {
            string manifestPath = Path.Combine(root, "module-manifest.json");
            NoLinks(manifestPath);
            manifest = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifest.Length is < 1 or > 65536) throw Invalid();
            using var document = JsonDocument.Parse(manifest);
            RejectDuplicateKeys(document.RootElement);
            var value = document.RootElement.Deserialize<NativeModuleDescription>(Json) ?? throw Invalid();
            if (value.SchemaVersion != 1 || value.Protocol != 1 || value.CorelMajor != 27 || value.ModuleVersion != "0.4.0"
                || !Hash(value.SourceSha256) || !Hash(value.GmsSha256) || value.File != "CorelMcp.Runtime.gms"
                || value.ProjectName != "CorelMcp_Runtime_v1_" + value.SourceSha256[..12]
                || value.Operations is not { Length: > 0 and <= 13 }
                || value.Operations.Distinct(StringComparer.Ordinal).Count() != value.Operations.Length
                || !value.Operations.Contains("handshake", StringComparer.Ordinal)
                || value.Operations.Any(op => !AllowedOperations.Contains(op))) throw Invalid();
            string path = Path.Combine(root, value.File);
            NoLinks(path);
            module = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (module.Length is < 1 or > 16777216) throw Invalid();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(module), Convert.FromHexString(value.GmsSha256))) throw Invalid();
            module.Position = 0;
            return new(value, path, manifest, module);
        }
        catch { module?.Dispose(); manifest?.Dispose(); throw; }
    }

    private static bool Hash(string value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    private static InvalidDataException Invalid() => new("Модуль VBA не прошёл проверку версии, контракта или целостности; выполнение запрещено.");
    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }
    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!keys.Add(property.Name)) throw Invalid();
    }
}
