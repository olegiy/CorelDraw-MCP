using System.Text;
using System.Text.Json;
using CorelMcp.Worker.Operations;
using CorelMcp.Worker.Validation;

namespace CorelMcp.Worker.Hybrid;

internal sealed record BatchCreateItem(string ItemKey, IDrawingCommand Creation, FillSpec? Fill, OutlineSpec? Outline, double RotationDeg);
internal sealed record CreateBatchCommand(string LayerId, BatchCreateItem[] Items):IDrawingCommand;
internal sealed record NativeTextEdit(int Kind,string Original,string Expected,string? Text=null,string? FontFamily=null,double? SizePt=null,bool? Bold=null,bool? Italic=null,string? Alignment=null,double? Spacing=null,int[]? Positions=null,int OldLength=0);
internal sealed record BatchUpdateItem(int StaticId, FillSpec? Fill, OutlineSpec? Outline, TransformCommand? Transform,NativeTextEdit? TextEdit=null);
internal sealed record UpdateBatchCommand(BatchUpdateItem[] Items):IDrawingCommand;

internal static class NativeBatchContracts
{
    public static DrawingArguments Parse(string tool,JsonElement args)
    {
        try
        {
            if(tool is not ("corel_create_batch" or "corel_update_batch") || Encoding.UTF8.GetByteCount(args.GetRawText())>1048576)throw Invalid();
            ValidateTree(args);
            var fields=args.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
            var bindingNames=new HashSet<string>(["request_id","document_id","page_index","deadline_seconds"]);
            var permitted=new HashSet<string>(bindingNames){"items"};if(tool=="corel_create_batch")permitted.Add("layer_id");
            if(fields.Keys.Any(k=>!permitted.Contains(k)))throw Invalid();
            var bindingElement=JsonSerializer.SerializeToElement(fields.Where(p=>bindingNames.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value));
            var binding=DrawingContracts.Read<DrawingBinding>(bindingElement);
            OperationContracts.Require(binding.RequestId,200,"request_id");OperationContracts.Require(binding.DocumentId,100,"document_id");
            OperationContracts.Page(binding.PageIndex);OperationContracts.Range(binding.DeadlineSeconds,1,3600,"deadline_seconds");
            var items=fields["items"].EnumerateArray().ToArray();if(items.Length is <1 or >1000)throw Invalid();
            IDrawingCommand ParseExisting(string name,Dictionary<string,object?> values)
            {
                foreach(var pair in bindingElement.EnumerateObject())values.Add(pair.Name,pair.Value);
                return DrawingContracts.Parse(name,JsonSerializer.SerializeToElement(values)).Command;
            }
            StyleCommand? Style(Dictionary<string,JsonElement> item,int id)
            {
                var payload=new Dictionary<string,object?>{{"static_ids",new[]{id}}};
                foreach(var key in new[]{"fill","outline"})if(item.TryGetValue(key,out var value))payload.Add(key,value);
                return payload.Count==1?null:(StyleCommand)ParseExisting("corel_style_shapes",payload);
            }
            IDrawingCommand command;
            if(tool=="corel_create_batch")
            {
                string layer=fields["layer_id"].GetString()!;OperationContracts.Require(layer,100,"layer_id");
                var result=new List<BatchCreateItem>();var keys=new HashSet<string>(StringComparer.Ordinal);long textLength=0;
                foreach(var raw in items)
                {
                    var item=raw.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
                    string key=item["item_key"].GetString()!;CorelText.Validate(key);OperationContracts.Require(key,100,"item_key");if(!keys.Add(key))throw Invalid();
                    double rotation=item.TryGetValue("rotation_deg",out var angle)?angle.GetDouble():0;
                    if(!double.IsFinite(rotation)||rotation is < -360000 or >360000)throw Invalid();
                    var geometry=item.Where(p=>p.Key is not ("item_key" or "fill" or "outline" or "rotation_deg")).ToDictionary(p=>p.Key,p=>(object?)p.Value);
                    IDrawingCommand creation;
                    if(item["kind"].GetString()=="artistic_text")
                    {
                        geometry["kind"]="artistic";geometry["layer_id"]=layer;
                        creation=ParseExisting("corel_create_text",geometry);textLength+=((CreateTextCommand)creation).Text.Length;
                    }
                    else creation=ParseExisting("corel_create_shapes",new(){{"layer_id",layer},{"items",new[]{geometry}}});
                    var style=Style(item,1);result.Add(new(key,creation,style?.Fill,style?.Outline,rotation));
                }
                if(textLength>100000)throw Invalid();command=new CreateBatchCommand(layer,result.ToArray());
            }
            else
            {
                var result=new List<BatchUpdateItem>();var ids=new HashSet<int>();
                foreach(var raw in items)
                {
                    var item=raw.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
                    if(item.Keys.Any(k=>k is not ("static_id" or "fill" or "outline" or "transform")))throw Invalid();
                    int id=item["static_id"].GetInt32();if(id<=0||!ids.Add(id))throw Invalid();
                    var style=Style(item,id);TransformCommand? transform=null;
                    if(item.TryGetValue("transform",out var value))
                    {
                        var payload=value.EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value);
                        payload.Add("static_ids",new[]{id});transform=(TransformCommand)ParseExisting("corel_transform_shapes",payload);
                    }
                    if(style is null&&transform is null)throw Invalid();result.Add(new(id,style?.Fill,style?.Outline,transform));
                }
                command=new UpdateBatchCommand(result.ToArray());
            }
            return new(binding.RequestId,binding.DocumentId,binding.PageIndex,command,binding.DeadlineSeconds);
        }
        catch(OperationFailure){throw;}
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException or FormatException or TextValidationException)
        {throw Invalid();}
    }
    private static OperationFailure Invalid()=>new("INVALID_INPUT","Пакет не соответствует строгому контракту; изменения не выполнены.");
    private static void ValidateTree(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Null)throw Invalid();
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw Invalid();ValidateTree(p.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())ValidateTree(item);
    }
}
