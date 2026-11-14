using CorelMcp.Worker.Operations;
using Xunit;
namespace CorelMcp.Worker.Tests;
public sealed class GeometryProofTests
{
    private static GeometryShapeProof Shape(int id=4)=>new(id,3,[10,20,30,40],15,[],[new(1,[0,0,10,10])]);
    [Fact] public void CachedProofRetainsExactIdsStructureSegmentsAndPhysicalToleranceWithoutSourceCom()
    {
        var original=Shape();GeometryIsolation.Verify(original,Shape(),25.4);
        foreach(var copy in new[]{Shape(5),Shape() with {Type=2},Shape() with {Rotation=16},Shape() with {Bounds=[10,20,31,40]},Shape() with {Segments=[new(1,[0,0,10,11])]},Shape() with {Bounds=[double.NaN,20,30,40]}})
            Assert.Equal("COPY_GEOMETRY_MISMATCH",Assert.Throws<OperationFailure>(()=>GeometryIsolation.Verify(original,copy,25.4)).Code);
        GeometryIsolation.Verify(original,Shape() with {Bounds=[10+1e-10,20,30,40]},25.4);
        var group=Shape() with {Type=7,Children=[Shape(8)],Segments=[]};
        Assert.Throws<OperationFailure>(()=>GeometryIsolation.Verify(group,group with {Children=[Shape(9)]},1));
    }
}
