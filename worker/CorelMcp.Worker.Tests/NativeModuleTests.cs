using System.Security.Cryptography;
using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class NativeModuleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CorelMcp_NativeContract_" + Guid.NewGuid().ToString("N"));
    public NativeModuleTests() { Directory.CreateDirectory(directory); }
    private void Fixture(string? file = null, int protocol = 1, string? hash = null, string[]? operations = null)
    {
        File.WriteAllText(Path.Combine(directory, "CorelMcp.Runtime.gms"), "own-test-bytes");
        File.WriteAllText(Path.Combine(directory, "module-manifest.json"), JsonSerializer.Serialize(new {
            schema_version = 1, protocol, module_version = "0.4.0", corel_major = 27,
            source_sha256 = new string('a', 64), project_name = "CorelMcp_Runtime_v1_aaaaaaaaaaaa",
            file = file ?? "CorelMcp.Runtime.gms", gms_sha256 = hash ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "CorelMcp.Runtime.gms")))),
            operations = operations ?? ["handshake"]
        }));
    }
    [Fact] public void MissingModuleFailsBeforeAnyComWork()
    { Assert.ThrowsAny<Exception>(() => NativeModuleManifest.LoadAndVerify(directory)); }
    [Theory]
    [InlineData("../other.gms", 1)]
    [InlineData("CorelMcp.Runtime.gms", 2)]
    public void InvalidBindingIsRejected(string file, int protocol)
    { Fixture(file, protocol); Assert.ThrowsAny<Exception>(() => NativeModuleManifest.LoadAndVerify(directory)); }
    [Fact] public void WrongHashAndUnknownOperationsAreRejected()
    {
        Fixture(hash: new string('0', 64)); Assert.ThrowsAny<Exception>(() => NativeModuleManifest.LoadAndVerify(directory));
        Fixture(operations: ["handshake", "execute_vba"]); Assert.ThrowsAny<Exception>(() => NativeModuleManifest.LoadAndVerify(directory));
    }
    [Fact] public void VerifiedModuleHoldsBothFilesAgainstModification()
    {
        Fixture();
        using (var module = NativeModuleManifest.LoadAndVerify(directory))
        {
            Assert.Equal("CorelMcp_Runtime_v1_aaaaaaaaaaaa", module.ProjectName);
            Assert.Equal(1, module.Protocol);
            Assert.Throws<IOException>(() => File.WriteAllText(module.FullPath, "tampered"));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, "module-manifest.json"), "{}"));
        }
        File.WriteAllText(Path.Combine(directory, "CorelMcp.Runtime.gms"), "released");
    }
    [Fact] public void UnknownManifestFieldIsNotSilentlyAccepted()
    {
        Fixture(); var path = Path.Combine(directory, "module-manifest.json");
        var text = File.ReadAllText(path); File.WriteAllText(path, text[..^1] + ",\"macro\":\"Anything.Main\"}");
        Assert.ThrowsAny<Exception>(() => NativeModuleManifest.LoadAndVerify(directory));
    }
    public void Dispose() { Directory.Delete(directory, true); }
}
