using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker.Hybrid;

internal static class NativeDrawing
{
    public static Action Prepare(DrawingTarget target,IDrawingCommand command,VGCore.Shape[]? prevalidated=null)
    {
        var c=target.Context;
        if(c.Session.ExecutionMode!=ExecutionMode.Hybrid)throw new OperationFailure("HYBRID_REQUIRED","Пакет требует включённого проверенного VBA runtime.");
        c.ExecutionEngine="vba";
        object[,] rows;VGCore.Layer layer;int[] ids;string[] keys;string[] layerIds;
        var range=c.Track(c.App.CreateShapeRange());
        double left=target.X(0),bottom=target.Y(0),factor=target.U(1);
        string operation;
        if(command is CreateBatchCommand create)
        {
            operation="create_batch";layer=target.Layer(create.LayerId);ids=[];keys=create.Items.Select(i=>i.ItemKey).ToArray();
            layerIds=create.Items.Select(_=>create.LayerId).ToArray();
            if(prevalidated is not null)
            {
                foreach(var source in prevalidated)range.Add(source);
                layerIds=create.Items.Select(i=>i.Creation is NativeCloneCommand clone
                    ?c.Registry.LayerId(c.Target!,target.Page,c.Track(prevalidated[clone.SourceIndex].Layer)):create.LayerId).ToArray();
            }
            rows=NativeRowBuilder.Create(create,left,bottom,factor);
        }
        else if(command is UpdateBatchCommand update)
        {
            operation=update.Items.All(i=>i.TextEdit?.Kind==4)?"convert_text":"update_batch";ids=update.Items.Select(i=>i.StaticId).ToArray();keys=ids.Select(_=>"").ToArray();
            var shapes=prevalidated??target.Resolve(ids,groups:false);
            if(shapes.Length!=ids.Length)throw new OperationFailure("TARGET_CHANGED","Набор целей изменился до native batch.");
            layerIds=shapes.Select(s=>c.Registry.LayerId(c.Target!,target.Page,c.Track(s.Layer))).ToArray();
            long textUnits=0;
            foreach(var shape in shapes)if(shape.Type==VGCore.cdrShapeType.cdrTextShape)
            {
                string text=c.Track(c.Track(shape.Text).Story).WideText;
                try{Validation.CorelText.Validate(text);}catch(Validation.TextValidationException){throw new OperationFailure("TEXT_SOURCE_UNSUPPORTED","Текст источника не поддерживается безопасным native snapshot.");}
                textUnits+=text.Length;
            }
            if(textUnits>100000)throw new OperationFailure("TEXT_LIMIT_EXCEEDED","Суммарный текст результата native batch превышает 100000 UTF-16; пакет не применён.");
            foreach(var shape in shapes)range.Add(shape);
            layer=c.Track(shapes[0].Layer);rows=NativeRowBuilder.Update(update,left,bottom,factor);
        }
        else throw new OperationFailure("UNKNOWN_TOOL","Неподдержанная пакетная операция.");
        var runtime=c.Session.Native;
        var identity=runtime.EnsureReady(c);
        runtime.RequireOperation(operation);
        if(command is CreateBatchCommand textBatch)
        {
            var fonts=textBatch.Items.Select(i=>i.Creation).OfType<ArtisticTextCommand>().Select(t=>t.FontFamily).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            runtime.ValidateFonts(c,fonts);
        }
        if(command is UpdateBatchCommand editBatch)
            runtime.ValidateFonts(c,editBatch.Items.Select(i=>i.TextEdit?.FontFamily).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var batch=new PreparedNativeBatch(c,target.Document,target.Page,layer,range,rows,operation,keys,ids,left,bottom,factor);
        return ()=>
        {
            c.ExecutionEngine="vba";
            bool optimization=c.App.Optimization,events=c.App.EventsEnabled;
            try
            {
            c.App.Optimization=true;c.App.EventsEnabled=false;
            c.Stage("native_applying");
            var reply=runtime.Apply(batch);
            if(reply.Applied is null)c.Session.MarkNativeOutcomeUnknown();
            if(reply.Applied==true)
            {
                c.RecordMutation(reply.KnownBeforeStaticIds,reply.AffectedStaticIds);
            }
            else if(reply.Applied is null)
            {
                // A partially traversed converted subtree cannot prove which old IDs disappeared.
                // Completed mappings can; new IDs and known source mutations are explicit proofs.
                var deleted=operation=="convert_text"?reply.Items.Where(item=>!item.GetProperty("result_static_ids").EnumerateArray().Any(id=>id.GetInt32()==item.GetProperty("source_static_id").GetInt32()))
                    .Select(item=>item.GetProperty("source_static_id").GetInt32()):Enumerable.Empty<int>();
                c.RecordKnownChanges(reply.AffectedStaticIds.Concat(reply.KnownBeforeStaticIds),reply.CreatedStaticIds,deleted);
            }
            for(int i=0;i<reply.Items.Length;i++)
            {
                var info=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,object?>>(reply.Items[i].GetRawText())!;
                info["static_id"]=reply.Items[i].GetProperty("static_id").GetInt32();
                info["type"]=reply.Items[i].GetProperty("type").GetInt32();info["layer_id"]=layerIds[i];
                c.DrawingItems.Add(info);
            }
            if(operation=="convert_text")c.DrawingDetails["id_mappings"]=reply.Items.Select(item=>new {
                source_static_id=item.GetProperty("source_static_id").GetInt32(),
                result_static_ids=item.GetProperty("result_static_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray()
            }).ToArray();
            if(!reply.Ok&&reply.Applied is null)
                throw new OperationFailure("NATIVE_OUTCOME_UNKNOWN","Исход native batch не подтверждён; известные ID сохранены, повтор и новые мутации запрещены.",null);
            for(int i=0;i<reply.Items.Length;i++)
            {
                FillSpec? fill=command is CreateBatchCommand cb?cb.Items[i].Fill:((UpdateBatchCommand)command).Items[i].Fill;
                OutlineSpec? outline=command is CreateBatchCommand cb2?cb2.Items[i].Outline:((UpdateBatchCommand)command).Items[i].Outline;
                var observed=reply.Items[i];
                if(command is UpdateBatchCommand edits&&edits.Items[i].TextEdit is {Kind:not 4} edit)
                {
                    if(observed.GetProperty("text").GetString()!=edit.Expected
                        ||edit.SizePt is double size&&Math.Abs(observed.GetProperty("font_size_pt").GetDouble()-size)>.001
                        ||edit.Bold is bool bold&&observed.GetProperty("bold").GetBoolean()!=bold
                        ||edit.Italic is bool italic&&observed.GetProperty("italic").GetBoolean()!=italic
                        ||edit.Alignment is string alignment&&observed.GetProperty("alignment_native").GetInt32()!=(alignment switch {"left"=>1,"right"=>2,"center"=>3,"justify"=>4,_=>5})
                        ||edit.Spacing is double spacing&&(observed.GetProperty("line_spacing_type").GetInt32()!=(int)VGCore.cdrLineSpacingType.cdrPercentOfPointSizeLineSpacing||Math.Abs(observed.GetProperty("line_spacing").GetDouble()-spacing)>.001))
                        throw new OperationFailure("NATIVE_TEXT_MISMATCH","Фактический текст или формат отличается от запрошенного; повтор запрещён.",true);
                    if(edit.FontFamily is string font&&!string.Equals(observed.GetProperty("font_family").GetString(),font,StringComparison.OrdinalIgnoreCase))
                        throw new OperationFailure("FONT_SUBSTITUTED","CorelDRAW подставил другой шрифт; повтор запрещён.",true);
                }
                if(fill is NoFill&&observed.GetProperty("fill_type").GetInt32()!=0)throw Mismatch();
                if(fill is UniformFill f&&!MatchesColor(observed.GetProperty("fill_color"),f.Color))throw Mismatch();
                if(outline is NoOutline&&observed.GetProperty("outline_type").GetInt32()!=0)throw Mismatch();
                if(outline is UniformOutline o&&(!MatchesColor(observed.GetProperty("outline_color"),o.Color)
                    ||Math.Abs(observed.GetProperty("outline_width_mm").GetDouble()-o.WidthMm)>.001))throw Mismatch();
                if(command is CreateBatchCommand textCommand&&textCommand.Items[i].Creation is ArtisticTextCommand text
                    &&!string.Equals(observed.GetProperty("font_family").GetString(),text.FontFamily,StringComparison.OrdinalIgnoreCase))
                    throw new OperationFailure("FONT_SUBSTITUTED","CorelDRAW подставил другой шрифт; созданные ID сохранены, повтор запрещён.",true);
                if(command is CreateBatchCommand textCreate&&textCreate.Items[i].Creation is ArtisticTextCommand content
                    &&(observed.GetProperty("text").GetString()!=NativeText.StoredText(content.Text)||Math.Abs(observed.GetProperty("font_size_pt").GetDouble()-content.FontSizePt)>.001))
                    throw new OperationFailure("NATIVE_TEXT_MISMATCH","Фактический созданный текст отличается от запрошенного; созданные ID сохранены, повтор запрещён.",true);
            }
            c.DrawingDetails["execution_engine"]="vba";c.DrawingDetails["native_module"]=identity.Version;
            c.Stage("native_result_validated");
            if(!reply.Ok)
            {
                if(reply.Applied is null)
                {
                    c.Session.MarkNativeOutcomeUnknown();
                    throw new OperationFailure("NATIVE_OUTCOME_UNKNOWN","Исход native batch не подтверждён; повтор и новые мутации запрещены.",null);
                }
                string code=reply.ErrorMessage is "FONT_SUBSTITUTED" or "PAGE_CHANGED" or "TARGET_CHANGED" or "LOCKED_TARGET"?reply.ErrorMessage:reply.ErrorCode;
                throw new OperationFailure(code,"Ошибка пакетной операции CorelDRAW: "+reply.ErrorMessage,reply.Applied);
            }
            }
            finally
            {
                c.Cleanup.Try("native.events",()=>c.App.EventsEnabled=events);
                c.Cleanup.Try("native.optimization",()=>c.App.Optimization=optimization);
                if(!optimization)c.Cleanup.Try("native.refresh",()=>c.App.Refresh());
            }
        };
    }
    private static OperationFailure Mismatch()=>new("NATIVE_STYLE_MISMATCH","Фактический стиль отличается от запрошенного; известные изменения сохранены, повтор запрещён.",true);
    private static bool MatchesColor(System.Text.Json.JsonElement actual,ColorSpec expected)=>expected switch
    {
        RgbSpec rgb=>actual.GetProperty("model").GetString()=="rgb"&&actual.GetProperty("r").GetInt32()==rgb.R&&actual.GetProperty("g").GetInt32()==rgb.G&&actual.GetProperty("b").GetInt32()==rgb.B,
        CmykSpec cmyk=>actual.GetProperty("model").GetString()=="cmyk"&&actual.GetProperty("c").GetInt32()==cmyk.C&&actual.GetProperty("m").GetInt32()==cmyk.M&&actual.GetProperty("y").GetInt32()==cmyk.Y&&actual.GetProperty("k").GetInt32()==cmyk.K,
        _=>false
    };
}
