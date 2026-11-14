using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

internal static class NativeRowBuilder
{
    private static object[,] Rows(int count)
    {
        var data=new object[count,40];
        for(int i=0;i<count;i++){for(int j=0;j<40;j++)data[i,j]=0;data[i,0]="";data[i,6]="";data[i,7]="";data[i,31]=-1;data[i,32]=-1;data[i,33]=-1;}
        return data;
    }
    public static object[,] Create(CreateBatchCommand command,double left,double bottom,double factor)
    {
        var data=Rows(command.Items.Length);
        for(int i=0;i<command.Items.Length;i++)
        {
            var item=command.Items[i];data[i,0]=item.ItemKey;data[i,9]=item.RotationDeg;
            switch(item.Creation)
            {
                case NativeCloneCommand clone:
                    data[i,1]=5;data[i,2]=clone.DxMm*factor;data[i,3]=clone.DyMm*factor;data[i,26]=clone.SourceStaticId;data[i,39]=clone.SourceIndex+1;break;
                case CreateShapesCommand {Items:[RectangleItem r]}:
                    data[i,1]=1;data[i,2]=left+r.XMm*factor;data[i,3]=bottom+r.YMm*factor;data[i,4]=r.WidthMm*factor;data[i,5]=r.HeightMm*factor;break;
                case CreateShapesCommand {Items:[EllipseItem e]}:
                    data[i,1]=2;data[i,2]=left+e.XMm*factor;data[i,3]=bottom+e.YMm*factor;data[i,4]=e.WidthMm*factor;data[i,5]=e.HeightMm*factor;break;
                case CreateShapesCommand {Items:[LineItem l]}:
                    data[i,1]=3;data[i,2]=left+l.X1Mm*factor;data[i,3]=bottom+l.Y1Mm*factor;data[i,4]=left+l.X2Mm*factor;data[i,5]=bottom+l.Y2Mm*factor;break;
                case ArtisticTextCommand text:
                    data[i,1]=4;data[i,2]=left+text.XMm*factor;data[i,3]=bottom+text.YMm*factor;data[i,6]=text.Text;data[i,7]=text.FontFamily;data[i,8]=text.FontSizePt;break;
                default:throw new OperationFailure("INVALID_INPUT","Неподдержанный элемент native batch.");
            }
            Style(data,i,item.Fill,item.Outline,factor);
        }
        return data;
    }
    public static object[,] Update(UpdateBatchCommand command,double left,double bottom,double factor)
    {
        var data=Rows(command.Items.Length);
        for(int i=0;i<command.Items.Length;i++)
        {
            var item=command.Items[i];data[i,26]=item.StaticId;Style(data,i,item.Fill,item.Outline,factor);
            if(item.TextEdit is { } edit)
            {
                data[i,27]=edit.Kind;data[i,28]=edit.Text??"";data[i,29]=edit.FontFamily??"";data[i,30]=edit.SizePt??0;
                data[i,31]=edit.Bold is null?-1:edit.Bold.Value?1:0;data[i,32]=edit.Italic is null?-1:edit.Italic.Value?1:0;
                data[i,33]=edit.Alignment switch {null=>-1,"left"=>0,"center"=>1,"right"=>2,"justify"=>3,"full_justify"=>4,_=>throw new OperationFailure("INVALID_INPUT","Неподдержанное выравнивание текста.")};
                data[i,34]=edit.Spacing??0;data[i,35]=edit.Original;data[i,36]=edit.Positions??Array.Empty<int>();data[i,37]=edit.Positions?.Length??0;data[i,38]=edit.OldLength;
            }
            switch(item.Transform)
            {
                case MoveCommand m:data[i,21]=1;data[i,22]=m.DxMm*factor;data[i,23]=m.DyMm*factor;break;
                case RotateCommand r:data[i,21]=2;data[i,22]=r.AngleDeg;data[i,24]=left+r.AnchorXMm*factor;data[i,25]=bottom+r.AnchorYMm*factor;break;
                case ScaleCommand s:data[i,21]=3;data[i,22]=s.Sx;data[i,23]=s.Sy;data[i,24]=left+s.AnchorXMm*factor;data[i,25]=bottom+s.AnchorYMm*factor;break;
                case FlipCommand f:data[i,21]=f.Axis=="horizontal"?4:5;data[i,24]=left+f.AnchorXMm*factor;data[i,25]=bottom+f.AnchorYMm*factor;break;
            }
        }
        return data;
    }
    private static void Style(object[,] data,int row,FillSpec? fill,OutlineSpec? outline,double factor)
    {
        if(fill is NoFill)data[row,10]=1;
        if(fill is UniformFill f)Color(data,row,10,f.Color);
        if(outline is NoOutline)data[row,15]=1;
        if(outline is UniformOutline o){Color(data,row,15,o.Color);data[row,20]=o.WidthMm*factor;}
    }
    private static void Color(object[,] data,int row,int column,ColorSpec color)
    {
        switch(color)
        {
            case RgbSpec c:data[row,column]=2;data[row,column+1]=c.R;data[row,column+2]=c.G;data[row,column+3]=c.B;break;
            case CmykSpec c:data[row,column]=3;data[row,column+1]=c.C;data[row,column+2]=c.M;data[row,column+3]=c.Y;data[row,column+4]=c.K;break;
            default:throw new OperationFailure("INVALID_INPUT","Неподдержанная модель цвета.");
        }
    }
}
