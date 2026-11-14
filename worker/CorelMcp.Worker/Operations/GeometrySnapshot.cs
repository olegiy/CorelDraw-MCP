using System.Text.Json;

namespace CorelMcp.Worker.Operations;

internal static class GeometrySnapshot
{
    public static object Execute(OperationContext context, MeasureArguments args)
    {
        context.Stage("extracting_geometry");
        var doc = context.Target!.Document;
        var page = context.Registry.Page(context.Target, context.Scope, args.PageIndex);
        VGCore.Shape[] targets;
        if (args.StaticIds is not null) targets = ShapeTraversal.Resolve(context, page, args.StaticIds);
        else
        {
            if (page.Index != context.Track(doc.ActivePage).Index) throw new OperationFailure("SELECTION_PAGE_MISMATCH", "Выделение доступно только на текущей странице.");
            var selection = context.Track(doc.SelectionRange);
            if (selection.Count == 0) throw new OperationFailure("EMPTY_SELECTION", "В документе нет выделенных объектов.");
            if (selection.Count > 100) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Допускается не более 100 целей.");
            targets = Enumerable.Range(1, selection.Count).Select(index => context.Track(selection[index])).ToArray();
            if (targets.Any(shape => context.Track(shape.Page).Index != page.Index)) throw new OperationFailure("SELECTION_PAGE_MISMATCH", "Выделение содержит объекты другой страницы.");
        }
        int visited = 0;
        List<string> unsupported = [];
        foreach (var target in targets) ValidateSupported(context, target, unsupported, ref visited, 0);
        if (unsupported.Count > 0) throw new OperationFailure("UNSUPPORTED_GEOMETRY", "Неподдерживаемые объекты: " + string.Join("; ", unsupported));
        var mm = context.App.ConvertUnits(1, doc.Unit, VGCore.cdrUnit.cdrMillimeter);
        if (!double.IsFinite(mm) || mm <= 0) throw new OperationFailure("GEOMETRY_INVALID_UNITS", "Нельзя определить физические единицы документа.");
        var directory = Path.GetFullPath(context.Request.RunDirectory);
        WorkerArtifacts.EnsureDirectory(directory);
        var part = Path.Combine(directory, "geometry-" + Guid.NewGuid().ToString("N") + ".part");
        var output = Path.ChangeExtension(part, ".json");
        VGCore.Document? temporary = null;
        bool createdPart = false;
        using var privateContext=new OperationContext(context.Session,context.Request,_=>{},context.Cleanup);
        using var closeHold=context.Session.CreateScope();
        context.Stage("geometry_source_snapshot");
        var proofs=GeometryIsolation.Capture(context,targets);
        var sourceIds=proofs.Select(p=>p.StaticId).ToArray();
        try
        {
            context.Stage("geometry_cloning");
            temporary = closeHold.Track(GeometryIsolation.Clone(privateContext, doc, targets));
            var tempPage = privateContext.Track(temporary.ActivePage);
            context.Stage("geometry_resolving_private");
            var copiedShapes=ShapeTraversal.Resolve(privateContext,tempPage,sourceIds);
            context.Stage("geometry_verifying_private");
            var copiedProofs=GeometryIsolation.Capture(privateContext,copiedShapes);
            for(int i=0;i<proofs.Length;i++)GeometryIsolation.Verify(proofs[i],copiedProofs[i],mm);
            var copies=sourceIds.Select((id,i)=>(id,shape:copiedShapes[i])).ToDictionary(p=>p.id,p=>p.shape);
            if(context.Session.ExecutionMode==Hybrid.ExecutionMode.Hybrid)
            {
                context.Stage("native_geometry_extracting");
                var mappings=sourceIds.Select(id=>new Hybrid.GeometryTargetMap(id,copies[id])).ToArray();
                context.ExecutionEngine="vba";
                string path=Hybrid.NativeGeometry.Extract(context,temporary,mappings,mm);
                context.Artifacts.Add(path);
                return Dto.Of(("snapshot_path",path),("original_state_preserved",true),("execution_engine","vba"));
            }
            using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                context.Stage("geometry_serializing");
                createdPart = true;
                using var bounded = new LimitedWriteStream(file, 64 * 1024 * 1024, true, "GEOMETRY_LIMIT_EXCEEDED");
                using var writer = new Utf8JsonWriter(bounded);
                int segments = 0;
                writer.WriteStartObject(); writer.WriteStartArray("objects");
                foreach (var sourceId in sourceIds)
                {
                    var root = copies[sourceId];
                    var working = privateContext.Track(root.Duplicate(0, 0));
                    working.MoveToLayer(privateContext.Track(temporary.ActiveLayer));
                    writer.WriteStartObject(); writer.WriteNumber("static_id", sourceId); writer.WriteStartArray("segments");
                    int before = segments;
                    WriteShape(privateContext, writer, working, mm, ref segments, 0, file);
                    if (segments == before) throw new OperationFailure("UNSUPPORTED_GEOMETRY", "Объект не содержит измеряемой геометрии.");
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                if (file.Length > 64 * 1024 * 1024) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Снимок превышает 64 МиБ.");
            }
            File.Move(part, output);
            context.Artifacts.Add(output);
            return Dto.Of(("snapshot_path", output), ("original_state_preserved", true));
        }
        finally
        {
            bool released=false;context.Cleanup.Try("geometry.private_references",()=>{privateContext.Dispose();released=true;});
            if (temporary is not null&&released) context.Cleanup.Try("geometry.temporary_document", () => { temporary.Dirty = false; temporary.Close(); });
            if (createdPart && File.Exists(part)) context.Cleanup.Try("geometry.partial_file", () => File.Delete(part));
        }
    }
    private static void ValidateSupported(OperationContext context, VGCore.Shape shape, List<string> errors, ref int visited, int depth)
    {
        if (depth > 32 || ++visited > 50000) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышен лимит проверки геометрии.");
        var type = (int)shape.Type;
        var id = shape.StaticID;
        if (!shape.Visible || !context.Track(shape.Layer).Visible) errors.Add("StaticID=" + id + ": объект или слой скрыт");
        if (type is not (1 or 2 or 3 or 4 or 6 or 7)) errors.Add("StaticID=" + id + ": неподдерживаемый тип");
        if (context.Track(shape.Effects).Count > 0) errors.Add("StaticID=" + id + ": эффекты");
        var clip = shape.PowerClip;
        if (clip is not null) { context.Track(clip); errors.Add("StaticID=" + id + ": PowerClip"); }
        if ((int)context.Track(shape.Transparency).Type != 0) errors.Add("StaticID=" + id + ": прозрачность");
        if (type != 7 && (int)context.Track(shape.Fill).Type == 0 && (int)context.Track(shape.Outline).Type == 0) errors.Add("StaticID=" + id + ": нет видимой заливки или обводки");
        if (type == 7)
        {
            var children = context.Track(shape.Shapes);
            for (int index = 1; index <= children.Count; index++) ValidateSupported(context, context.Track(children[index]), errors, ref visited, depth + 1);
        }
    }
    private static void WriteShape(OperationContext context, Utf8JsonWriter writer, VGCore.Shape shape, double mm, ref int segments, int depth, FileStream file)
    {
        if (depth > 32) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышена глубина извлечения геометрии.");
        if (shape.Type == VGCore.cdrShapeType.cdrGroupShape)
        {
            var children = context.Track(shape.Shapes);
            var items = Enumerable.Range(1, children.Count).Select(index => context.Track(children[index])).ToArray();
            foreach (var child in items) WriteShape(context, writer, child, mm, ref segments, depth + 1, file);
            return;
        }
        var outline = context.Track(shape.Outline);
        if ((int)outline.Type != 0)
        {
            var stroke = context.Track(outline.ConvertToObject());
            WriteShape(context, writer, stroke, mm, ref segments, depth + 1, file);
        }
        if ((int)context.Track(shape.Fill).Type == 0) return;
        if (shape.Type != VGCore.cdrShapeType.cdrCurveShape) shape.ConvertToCurves();
        if (shape.Type == VGCore.cdrShapeType.cdrGroupShape) { WriteShape(context, writer, shape, mm, ref segments, depth + 1, file); return; }
        if (shape.Type != VGCore.cdrShapeType.cdrCurveShape) throw new OperationFailure("UNSUPPORTED_GEOMETRY", "Объект не преобразуется в кривые.");
        var collection = context.Track(context.Track(shape.Curve).Segments);
        int count = collection.Count;
        for (int index = 1; index <= count; index++)
        {
            if (++segments > 200000) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Превышен лимит 200000 сегментов.");
            var segment = context.Track(collection[index]);
            var start = context.Track(segment.StartNode); var end = context.Track(segment.EndNode);
            writer.WriteStartObject();
            if (segment.Type == VGCore.cdrSegmentType.cdrLineSegment)
            {
                writer.WriteString("kind", "line"); Point(writer, "p0", start.PositionX * mm, start.PositionY * mm); Point(writer, "p1", end.PositionX * mm, end.PositionY * mm);
            }
            else if (segment.Type == VGCore.cdrSegmentType.cdrCurveSegment)
            {
                writer.WriteString("kind", "cubic"); Point(writer, "p0", start.PositionX * mm, start.PositionY * mm);
                Point(writer, "p1", segment.StartingControlPointX * mm, segment.StartingControlPointY * mm);
                Point(writer, "p2", segment.EndingControlPointX * mm, segment.EndingControlPointY * mm); Point(writer, "p3", end.PositionX * mm, end.PositionY * mm);
            }
            else throw new OperationFailure("UNSUPPORTED_GEOMETRY", "Неизвестный тип сегмента.");
            writer.WriteEndObject();
            if (file.Position + writer.BytesPending > 64 * 1024 * 1024) throw new OperationFailure("GEOMETRY_LIMIT_EXCEEDED", "Снимок превышает 64 МиБ.");
        }
    }
    private static void Point(Utf8JsonWriter writer, string key, double x, double y)
    {
        writer.WriteStartObject(key); writer.WriteNumber("x", x); writer.WriteNumber("y", y); writer.WriteEndObject();
    }
}
