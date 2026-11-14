using System.Text;
using CorelMcp.Worker.Operations;
namespace CorelMcp.Worker.Hybrid;

internal static class NativeText
{
    public static Action? TryPrepareConvert(DrawingTarget target,ConvertCommand command)
    {
        var c=target.Context;if(c.Session.ExecutionMode!=ExecutionMode.Hybrid||command.StaticIds.Length<20)return null;
        var shapes=target.Resolve(command.StaticIds);
        if(shapes.Any(s=>s.Type!=VGCore.cdrShapeType.cdrTextShape))return null;
        long total=0;var items=new List<BatchUpdateItem>();
        foreach(var shape in shapes)
        {
            string original=c.Track(c.Track(shape.Text).Story).WideText;total+=original.Length;
            try{Validation.CorelText.Validate(original);}catch(Validation.TextValidationException){return null;}
            if(total>100000)return null;
            items.Add(new(shape.StaticID,null,null,null,new NativeTextEdit(4,original,original)));
        }
        return NativeDrawing.Prepare(target,new UpdateBatchCommand(items.ToArray()),shapes);
    }
    // CorelDRAW 27 WideText drops LF; CR is its stored paragraph separator.
    internal static string StoredText(string input)=>input.Replace("\n","",StringComparison.Ordinal);
    public static Action? TryPrepare(DrawingTarget target,EditTextCommand command)
    {
        var c=target.Context;
        if(c.Session.ExecutionMode!=ExecutionMode.Hybrid||command.StaticIds.Length<20)return null;
        var shapes=target.Resolve(command.StaticIds,groups:false);
        if(shapes.Any(s=>s.Type!=VGCore.cdrShapeType.cdrTextShape))throw new OperationFailure("NOT_TEXT","Все цели должны быть текстовыми фигурами.");
        var edits=new List<BatchUpdateItem>();long sourceUnits=0,resultUnits=0,matchCount=0;
        foreach(var shape in shapes)
        {
            var story=c.Track(c.Track(shape.Text).Story);string original=story.WideText;
            try{Validation.CorelText.Validate(original);}catch(Validation.TextValidationException){throw new OperationFailure("TEXT_SOURCE_UNSUPPORTED","Исходный текст содержит неподдержанные символы; пакет не применён.");}
            sourceUnits+=original.Length;
            NativeTextEdit edit;
            if(command is ReplaceLiteralCommand replace)
            {
                var matches=Operations.Text.Occurrences(original,replace.Old,replace.CaseSensitive);matchCount+=matches.Count;
                long length=(long)original.Length+(long)matches.Count*(replace.New.Length-replace.Old.Length);
                if(length>100000)throw new OperationFailure("TEXT_LIMIT_EXCEEDED","Результат замены превышает 100000 UTF-16.");
                if(sourceUnits>100000||resultUnits+length>100000||matchCount>10000)return null;
                var chars=c.Track(story.Characters);
                foreach(int offset in matches)
                    if(c.Track(chars[offset+1,replace.Old.Length]).WideText!=original.Substring(offset,replace.Old.Length))
                        throw new OperationFailure("TEXT_INDEX_UNSUPPORTED","Нативные индексы не соответствуют UTF-16; пакет не применён.");
                var expected=new StringBuilder();int cursor=0;
                foreach(int offset in matches){expected.Append(original,cursor,offset-cursor);expected.Append(StoredText(replace.New));cursor=offset+replace.Old.Length;}
                expected.Append(original,cursor,original.Length-cursor);
                edit=new(3,original,expected.ToString(),Text:replace.New,Positions:matches.ToArray(),OldLength:replace.Old.Length);
            }
            else if(command is SetTextCommand set)edit=new(1,original,StoredText(set.Text),Text:set.Text);
            else if(command is FormatTextCommand format)edit=new(2,original,original,FontFamily:format.FontFamily,SizePt:format.FontSizePt,Bold:format.Bold,Italic:format.Italic,Alignment:format.Alignment,Spacing:format.LineSpacingPercent);
            else return null;
            resultUnits+=edit.Expected.Length;
            if(sourceUnits>100000||resultUnits>100000)return null;
            edits.Add(new(shape.StaticID,null,null,null,edit));
        }
        if(command is ReplaceLiteralCommand&&matchCount==0)return null;
        c.Session.Native.EnsureReady(c);
        c.Session.Native.RequireOperation(command is ReplaceLiteralCommand?"replace_literal":command is SetTextCommand?"set_text":"format_text");
        var selected=edits.Select((edit,index)=>(edit,index)).Where(pair=>command is not ReplaceLiteralCommand||pair.edit.TextEdit!.Positions!.Length>0).ToArray();
        return NativeDrawing.Prepare(target,new UpdateBatchCommand(selected.Select(pair=>pair.edit).ToArray()),selected.Select(pair=>shapes[pair.index]).ToArray());
    }
}
