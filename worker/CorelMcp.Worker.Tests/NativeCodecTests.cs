using CorelMcp.Worker.Hybrid;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class NativeCodecTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static NativeModuleDescription Module => new(1, 1, "0.4.0", 27, new string('a',64), "CorelMcp_Runtime_v1_aaaaaaaaaaaa", "CorelMcp.Runtime.gms", new string('b',64), ["handshake"]);
    private static string Reply => "{\"protocol\":1,\"module_version\":\"0.4.0\",\"source_sha256\":\""+new string('a',64)+"\",\"nonce\":\""+Nonce+"\",\"corel_major\":27,\"operations\":[\"handshake\"]}";
    [Fact] public void HandshakeBindsModuleAndNonce() => Assert.Equal(Nonce, NativeCallCodec.ReadHandshake(Reply, Nonce, Module).Nonce);
    [Theory]
    [InlineData("nonce", "wrong")]
    [InlineData("module_version", "0.2.0")]
    [InlineData("source_sha256", "bad")]
    public void MismatchedIdentityIsRejected(string field, string value)
    {
        var data=System.Text.Json.Nodes.JsonNode.Parse(Reply)!; data[field]=value;
        Assert.ThrowsAny<Exception>(()=>NativeCallCodec.ReadHandshake(data.ToJsonString(),Nonce,Module));
    }
    [Fact] public void MalformedUnknownDuplicateAndOversizedRepliesAreRejected()
    {
        foreach(string raw in new[]{Reply[..^1],Reply[..^1]+",\"code\":\"anything\"}", Reply[..^1]+",\"protocol\":1}",new string(' ',65537)})
            Assert.ThrowsAny<Exception>(()=>NativeCallCodec.ReadHandshake(raw,Nonce,Module));
    }
}
