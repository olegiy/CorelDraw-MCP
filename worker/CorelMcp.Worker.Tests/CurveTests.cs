using System.Globalization;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class CurveTests
{
    [Fact]
    public void RevisionIncludesExactCoordinatesTopologyNodeTypesControlsAndTransform()
    {
        var node = new CurveNodeData(1, 1, 1, new(0.25, 2), "cusp");
        var segment = new CurveSegmentData(1, 1, "cubic", new(0, 0), new(10, 0), new(0, 10), new(10, 10));
        var path = new CurvePathData(1, false, [node, node with { NodeIndex = 2, AbsoluteIndex = 2, Position = new(10, 0) }], [segment]);
        var data = new CurveData([1, 0, 0, 1, 0, 0], [path]); var revision = data.Revision();
        Assert.NotEqual(revision, (data with { Matrix = [1, 0, 0, 1, double.Epsilon, 0] }).Revision());
        Assert.NotEqual(revision, (data with { Paths = [path with { Closed = true }] }).Revision());
        Assert.NotEqual(revision, (data with { Paths = [path with { Nodes = [node with { Position = new(Math.BitIncrement(0.25), 2) }, path.Nodes[1]] }] }).Revision());
        Assert.NotEqual(revision, (data with { Paths = [path with { Nodes = [node with { NodeType = "smooth" }, path.Nodes[1]] }] }).Revision());
        Assert.NotEqual(revision, (data with { Paths = [path with { Segments = [segment with { Control1 = new(0, Math.BitIncrement(10)) }] }] }).Revision());
        Assert.NotEqual(revision, (data with { Paths = [path with { Nodes = path.Nodes.Reverse().ToArray() }] }).Revision());
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU"); Assert.Equal(revision, data.Revision()); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); Assert.Equal(revision, data.Revision()); }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
