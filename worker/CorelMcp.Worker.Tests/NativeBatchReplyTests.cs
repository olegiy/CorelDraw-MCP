using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using Xunit;
namespace CorelMcp.Worker.Tests;
public sealed class NativeBatchReplyTests
{
    private const string Nonce="0123456789abcdef0123456789abcdef";
    private static string Reply(string nonce=Nonce,int id=4)=>JsonSerializer.Serialize(new {
        protocol=1,nonce,operation="create_batch",ok=true,applied=true,partial=false,created_static_ids=new[]{id},affected_static_ids=new[]{id},known_before_static_ids=Array.Empty<int>(),
        items=new[]{new {item_key="r",static_id=id,type=1,bounds_mm=new {left=0d,bottom=0d,width=12d,height=8d},rotation_deg=0d,fill_type=0,outline_type=0,outline_width_mm=0d}},error_code="",error_message=""
    });
    [Fact] public void ConfirmedCreateHasExactKeysAndPositiveUniqueIds()
    {
        var reply=NativeBatchResults.Read(Reply(),Nonce,"create_batch",["r"],[]);
        Assert.Equal(4,Assert.Single(reply.CreatedStaticIds));Assert.True(reply.Applied);
        Assert.ThrowsAny<Exception>(()=>NativeBatchResults.Read(Reply(id:-1),Nonce,"create_batch",["r"],[]));
        Assert.ThrowsAny<Exception>(()=>NativeBatchResults.Read(Reply("other"),Nonce,"create_batch",["r"],[]));
        Assert.ThrowsAny<Exception>(()=>NativeBatchResults.Read(Reply(),Nonce,"create_batch",["wrong"],[]));
    }
    [Fact] public void UpdateCannotInventCreatedIdsOrDifferentTargets()
    {
        Assert.ThrowsAny<Exception>(()=>NativeBatchResults.Read(Reply(),Nonce,"update_batch",[""],[42]));
    }
}
