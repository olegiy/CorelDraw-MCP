namespace CorelMcp.Worker.Operations;

internal static class Import
{
    public static Action Prepare(DrawingTarget target, ImportCommand args)
    {
        var c = target.Context;
        using var privateContext = new OperationContext(c.Session, c.Request, c.Stage, c.Cleanup);
        var destinationLayer = target.Layer(args.LayerId);
        var previousLayer = c.Track(target.Document.ActiveLayer);
        var input = ImportSource.Prepare(args, c.Request.RunDirectory);
        c.Artifacts.AddRange(input.Artifacts);
        var transferPath = Path.Combine(c.Request.RunDirectory, "transfer-" + Guid.NewGuid().ToString("N") + ".cdr");
        ImportSource.ValidateNativePath(transferPath);
        VGCore.Document? temporary = null;
        VGCore.Layer? transferLayer = null;
        string? temporaryId = null, transferLayerId = null;
        HashSet<int> wrapperIds = [];
        try
        {
            c.Stage("importing_private_document");
            temporary = privateContext.Track(c.App.CreateDocument());
            c.DeferCleanup("import.deferred_document", CloseDocument);
            temporaryId = c.Registry.Register(c.App, privateContext.Scope, temporary).Id;
            temporary.Unit = target.Document.Unit;
            temporary.DrawingOriginX = target.Document.DrawingOriginX;
            temporary.DrawingOriginY = target.Document.DrawingOriginY;
            var page = privateContext.Track(temporary.ActivePage);
            page.SetSize(target.Page.SizeWidth, target.Page.SizeHeight);
            var filter = privateContext.Track(privateContext.Track(temporary.ActiveLayer).ImportEx(input.Path, Format(args.Format), Options()));
            bool hasDialog = filter.HasDialog; // Capability only. ShowDialog is never called.
            filter.Reset(); filter.Finish();
            if (privateContext.Track(temporary.Pages).Count != 1)
                throw new OperationFailure("IMPORT_PAGE_COUNT_MISMATCH", "Фильтр создал неожиданное число страниц; целевой документ не изменён.");
            var all = ShapeTraversal.Walk(privateContext, privateContext.Track(page.Shapes)).ToArray();
            foreach (var item in all)
                if (item.Shape.Type == VGCore.cdrShapeType.cdrTextShape)
                {
                    // Release private text/font interfaces while their document is still alive.
                    // Corel's font objects cannot safely outlive closing the private import document.
                    using var textScope = c.Session.CreateScope();
                    var story = textScope.Track(textScope.Track(item.Shape.Text).Story);
                    try { Validation.CorelText.Validate(story.WideText); }
                    catch (Validation.TextValidationException error) { throw new OperationFailure(error.Code, error.Message); }
                }
            var roots = all.Where(item => item.ParentId is null).Select(item => item.Shape).ToArray();
            if (roots.Length is < 1 or > 100)
                throw new OperationFailure("IMPORT_SHAPE_LIMIT", "Импорт должен содержать от 1 до 100 корневых объектов.");
            var range = privateContext.Track(c.App.CreateShapeRange());
            foreach (var shape in roots) range.Add(shape);
            range.GetBoundingBox(out var x, out var y, out var w, out var h, false);
            if (!new[] { x, y, w, h }.All(double.IsFinite))
                throw new OperationFailure("INVALID_IMPORT_GEOMETRY", "Не удалось измерить импортированные объекты.");
            if (args.Fit is not null)
            {
                if (w <= 0 || h <= 0) throw new OperationFailure("ZERO_SIZE_BOUNDS", "Нельзя вписать геометрию с нулевым габаритом.");
                double scale = Math.Min(target.U(args.Fit.WidthMm) / w, target.U(args.Fit.HeightMm) / h);
                range.StretchEx(x, y, scale, scale, true);
            }
            // Cross-document CopyToLayer is unsupported in 27.2. Prepare a native CDR before touching the target.
            Dictionary<string, string> tags = [];
            foreach (var shape in roots)
            {
                var name = shape.Name ?? "";
                try { Validation.CorelText.Validate(name); }
                catch (Validation.TextValidationException) { throw new OperationFailure("IMPORT_UNSUPPORTED_NAME", "Имя импортированного объекта содержит неподдерживаемые символы."); }
                var tag = "CorelMcp_Import_" + Guid.NewGuid().ToString("N");
                tags.Add(tag, name); shape.Name = tag;
            }
            var save = privateContext.Track(c.App.CreateStructSaveAsOptions());
            save.Filter = VGCore.cdrFilter.cdrCDR; save.Overwrite = true;
            try { temporary.SaveAs(transferPath, save); }
            finally { if (File.Exists(transferPath)) c.Artifacts.Add(transferPath); }
            c.DrawingDetails["source_sha256"] = input.SourceSha256;
            c.DrawingDetails["source_identity"] = input.SourceIdentity;
            c.DrawingDetails["source_bytes"] = input.SourceBytes;
            c.DrawingDetails["pdf_page"] = args.PdfPage;
            c.DrawingDetails["filter_has_dialog"] = hasDialog;
            return () =>
            {
                try
                {
                    if (!destinationLayer.Editable) throw new OperationFailure("LOCKED_TARGET", "Целевой слой заблокирован после подготовки импорта.");
                    c.Mutating(); transferLayer = c.Track(target.Page.CreateLayer("CorelMcp_Import_" + Guid.NewGuid().ToString("N")));
                    c.DeferCleanup("import.deferred_layer", CloseLayer); c.RecordMutation([], []);
                    transferLayerId = c.Registry.LayerId(c.Target!, target.Page, transferLayer);
                    c.DrawingDetails["retained_transfer_layer_id"] = transferLayerId;
                    var native = c.Track(transferLayer.ImportEx(transferPath, VGCore.cdrFilter.cdrCDR, Options()));
                    native.Reset(); native.Finish();
                    var imported = ShapeTraversal.Walk(c, c.Track(transferLayer.Shapes)).ToArray();
                    var tagged = imported.Where(item => tags.ContainsKey(item.Shape.Name ?? "")).ToArray();
                    if (tagged.Length != tags.Count || tagged.Select(item => item.Shape.Name).Distinct().Count() != tags.Count)
                        throw new OperationFailure("IMPORT_PROVENANCE_MISMATCH", "Метки собственных объектов не подтверждены; перенос других объектов запрещён.", true);
                    var owned = tagged.SelectMany(item => target.TreeIds(item.Shape)).ToHashSet();
                    var byId = imported.ToDictionary(item => item.Shape.StaticID);
                    foreach (var item in tagged)
                    {
                        var parent = item.ParentId;
                        while (parent is not null) { wrapperIds.Add(parent.Value); parent = byId[parent.Value].ParentId; }
                    }
                    if (imported.Any(item => !owned.Contains(item.Shape.StaticID) && !wrapperIds.Contains(item.Shape.StaticID)))
                        throw new OperationFailure("IMPORT_PROVENANCE_MISMATCH", "В промежуточном слое есть объекты с неподтверждённой принадлежностью заданию.", true);
                    c.RecordMutation([], owned);
                    var result = c.Track(c.App.CreateShapeRange());
                    foreach (var tag in tags)
                    {
                        var shape = tagged.Single(item => item.Shape.Name == tag.Key).Shape;
                        shape.Name = tag.Value; result.Add(shape);
                    }
                    result.MoveToLayer(destinationLayer);
                    result.GetBoundingBox(out var left, out var bottom, out _, out _, false);
                    result.Move(target.X(args.XMm) - left, target.Y(args.YMm) - bottom);
                    var copied = Enumerable.Range(1, result.Count).Select(index => c.Track(result[index])).ToArray();
                    c.DrawingDetails["imported_root_static_ids"] = copied.Select(shape => shape.StaticID).ToArray();
                    target.Observe([], copied);
                    c.DrawingDetails["import_diagnostics"] = ImportDiagnostics.Read(c, copied);
                    result.GetBoundingBox(out var ax, out var ay, out var aw, out var ah, false);
                    var point = target.Point(ax, ay);
                    double mm = c.App.ConvertUnits(1, target.Document.Unit, VGCore.cdrUnit.cdrMillimeter);
                    c.DrawingDetails["import_bounds_mm"] = Inspection.Bounds(point.XMm, point.YMm, aw * mm, ah * mm, 1);
                    if (Math.Abs(point.XMm - args.XMm) > 0.001 || Math.Abs(point.YMm - args.YMm) > 0.001)
                        throw new OperationFailure("IMPORT_PLACEMENT_MISMATCH", "Фактическое размещение импорта отличается от запрошенного.", true);
                }
                finally { CloseLayer(); CloseDocument(); }
            };
        }
        catch { CloseLayer(); CloseDocument(); throw; }

        VGCore.StructImportOptions Options()
        {
            var options = c.Track(c.App.CreateStructImportOptions());
            options.LinkBitmapExternally = false; options.MaintainLayers = false;
            options.UseOPILinks = false; options.ExtractICCProfile = false; options.Mode = VGCore.cdrImportMode.cdrImportFull;
            return options;
        }
        void CloseLayer()
        {
            if (transferLayer is null) return;
            var owned = transferLayer; transferLayer = null;
            c.Cleanup.Try("import.transfer_layer", () =>
            {
                var remaining = ShapeTraversal.Walk(c, c.Track(owned.Shapes)).OrderByDescending(item => item.Depth).ToArray();
                foreach (var item in remaining)
                    if (wrapperIds.Contains(item.Shape.StaticID) && item.Shape.Type == VGCore.cdrShapeType.cdrGroupShape && c.Track(item.Shape.Shapes).Count == 0) item.Shape.Delete();
                if (c.Track(owned.Shapes).Count != 0)
                    throw new OperationFailure("IMPORT_LAYER_RETAINED", "Слой " + transferLayerId + " сохранён: принадлежность оставшихся объектов требует проверки.", null);
                owned.Delete(); c.DrawingDetails.Remove("retained_transfer_layer_id");
            });
            c.Cleanup.Try("import.active_layer", previousLayer.Activate);
        }
        void CloseDocument()
        {
            if (temporary is null) return;
            var owned = temporary; temporary = null;
            c.Cleanup.Try("import.temporary_document", () =>
            {
                try { privateContext.Dispose(); owned.Dirty = false; owned.Close(); c.Registry.Sync(c.App, c.Scope); }
                catch (Exception error) { throw new OperationFailure("TEMPORARY_DOCUMENT_RETAINED", "Закрытие собственного временного документа " + temporaryId + " не подтверждено: " + error.Message, null); }
            });
        }
    }
    private static VGCore.cdrFilter Format(string format) => format switch
    { "svg" => VGCore.cdrFilter.cdrSVG, "png" => VGCore.cdrFilter.cdrPNG, "jpeg" => VGCore.cdrFilter.cdrJPEG, _ => VGCore.cdrFilter.cdrPDF };
}
