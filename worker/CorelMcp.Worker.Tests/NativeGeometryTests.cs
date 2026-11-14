using CorelMcp.Worker.Hybrid;
using Xunit;
namespace CorelMcp.Worker.Tests;
public sealed class NativeGeometryTests
{
    private const string Nonce="0123456789abcdef0123456789abcdef";
    private const string Reply="{\"protocol\":1,\"nonce\":\"0123456789abcdef0123456789abcdef\",\"objects\":[{\"static_id\":4,\"segments\":[{\"kind\":\"line\",\"p0\":{\"x\":0,\"y\":0},\"p1\":{\"x\":1,\"y\":2}}]}]}";
    [Fact] public void GeometryReplyRequiresIdentityExactTargetOrderAndFiniteSegments()
    {
        NativeGeometry.Validate(Reply,Nonce,[4]);
        Assert.ThrowsAny<Exception>(()=>NativeGeometry.Validate(Reply,"wrong",[4]));
        Assert.ThrowsAny<Exception>(()=>NativeGeometry.Validate(Reply,Nonce,[5]));
        Assert.ThrowsAny<Exception>(()=>NativeGeometry.Validate(Reply.Replace("\"x\":1","\"x\":1e999"),Nonce,[4]));
        Assert.ThrowsAny<Exception>(()=>NativeGeometry.Validate(Reply.Replace("\"kind\":\"line\"","\"kind\":\"unknown\""),Nonce,[4]));
    }
}
