namespace CorelMcp.Worker.Hybrid;

internal sealed record PreparedNativeBatch(OperationContext Context,VGCore.Document Document,VGCore.Page Page,VGCore.Layer Layer,
    VGCore.ShapeRange Targets,object[,] Rows,string Operation,string[] Keys,int[] OriginalIds,double Left,double Bottom,double Factor);
