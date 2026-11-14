using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Operations;
using Xunit;
namespace CorelMcp.Worker.Tests;
public sealed class NativeRowsTests
{
    [Fact] public void CreationConvertsUnitsAndKeepsOrderAndExactChannels()
    {
        var rectangle=new CreateShapesCommand("layer",[new RectangleItem(25.4,50.8,12.7,6.35)]);
        var batch=new CreateBatchCommand("layer",[new("r",rectangle,new UniformFill(new RgbSpec(1,2,3)),new NoOutline(),30)]);
        var rows=NativeRowBuilder.Create(batch,10,20,1/25.4);
        Assert.Equal(1,rows.GetLength(0));Assert.Equal(40,rows.GetLength(1));
        Assert.Equal("r",rows[0,0]);Assert.Equal(11d,rows[0,2]);Assert.Equal(22d,rows[0,3]);
        Assert.Equal(.5d,Assert.IsType<double>(rows[0,4]),10);Assert.Equal(2,rows[0,10]);Assert.Equal(1,rows[0,11]);Assert.Equal(1,rows[0,15]);
    }
    [Fact] public void TextEditCarriesExactSourceAndPreparedOrdinalOffsetsAsData()
    {
        var edit=new NativeTextEdit(3,"Ёж ёж", "Лис Лис",Text:"Лис",Positions:[0,3],OldLength:2);
        var rows=NativeRowBuilder.Update(new UpdateBatchCommand([new(42,null,null,null,edit)]),0,0,1);
        Assert.Equal(3,rows[0,27]);Assert.Equal("Лис",rows[0,28]);Assert.Equal("Ёж ёж",rows[0,35]);
        Assert.Equal(new[]{0,3},Assert.IsType<int[]>(rows[0,36]));Assert.Equal(2,rows[0,37]);Assert.Equal(2,rows[0,38]);
    }
    [Fact] public void UpdatesUseExplicitAnchorsAndPreserveTargetIds()
    {
        var batch=new UpdateBatchCommand([new(42,null,null,new RotateCommand([42],25,10,20))]);
        var rows=NativeRowBuilder.Update(batch,100,200,2);
        Assert.Equal(42,rows[0,26]);Assert.Equal(2,rows[0,21]);Assert.Equal(25d,rows[0,22]);
        Assert.Equal(120d,rows[0,24]);Assert.Equal(240d,rows[0,25]);Assert.Equal(0,rows[0,10]);
    }
}
