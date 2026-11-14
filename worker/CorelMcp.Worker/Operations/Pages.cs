namespace CorelMcp.Worker.Operations;

internal static class Pages
{
    public static Action Prepare(DrawingTarget target,PageCommand args)
    {
        var c=target.Context;
        if(args is ResizePageCommand resize)
        {
            var page=c.Registry.ResolvePage(c.Target!,c.Scope,resize.PageId);
            if(c.Track(page.TreeNode).Handle!=c.Track(target.Page.TreeNode).Handle)throw new OperationFailure("PAGE_MISMATCH","page_id и page_index относятся к разным страницам.");
            return ()=>{c.Mutating();page.SetSize(target.U(resize.WidthMm),target.U(resize.HeightMm));c.RecordMutation([],[]);c.DrawingDetails["pages"]=new[]{Info(c,page)};c.Stage("mutation_observed");};
        }
        var add=(AddPagesCommand)args;int before=c.Track(target.Document.Pages).Count;
        return ()=>
        {
            c.Mutating();c.Track(target.Document.AddPages(add.Count));c.RecordMutation([],[]);
            var pages=c.Track(target.Document.Pages);List<object> created=[];c.DrawingDetails["pages"]=created;
            for(int index=before+1;index<=pages.Count;index++)created.Add(Info(c,c.Track(pages[index])));
            c.Stage("mutation_observed");
            if(pages.Count!=before+add.Count)throw new OperationFailure("PAGE_COUNT_MISMATCH","Число добавленных страниц отличается от запрошенного.",true);
            for(int index=before+1;index<=pages.Count;index++)
            {
                var page=c.Track(pages[index]);page.SetSize(target.U(add.WidthMm),target.U(add.HeightMm));
                created[index-before-1]=Info(c,page);c.Stage("mutation_observed");
            }
        };
    }
    private static object Info(OperationContext c,VGCore.Page page)=>Dto.Of(("page_id",c.Registry.PageId(c.Target!,page)),("index",page.Index),
        ("width_mm",c.App.ConvertUnits(page.SizeWidth,c.Target!.Document.Unit,VGCore.cdrUnit.cdrMillimeter)),("height_mm",c.App.ConvertUnits(page.SizeHeight,c.Target!.Document.Unit,VGCore.cdrUnit.cdrMillimeter)));
}
