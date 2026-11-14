using CorelMcp.Worker.Interop;

namespace CorelMcp.Worker.Operations;

internal static class Export
{
    public static object Preview(OperationContext context, PreviewArguments args)
        => Execute(context, "png", args.ResolveTarget(), args.PageIndex, Path.Combine(context.Request.RunDirectory, "preview.png"), 96, args.MaxSize, null);
    public static object Execute(OperationContext context, ExportArguments args)
    {
        var format = args.Format switch { "jpeg" => "jpg", "tif" => "tiff", _ => args.Format };
        return Execute(context, format, args.Range, args.PageIndex, Documents.OutputPath(args.Path, format), args.Dpi, null, args);
    }
    private static object Execute(OperationContext context, string format, string range, int? pageIndex, string destination, int dpi, int? maxSize, ExportArguments? args)
    {
        context.Stage("exporting");
        var doc = context.Target!.Document;
        var page = context.Registry.Page(context.Target, context.Scope, pageIndex);
        if (range == "selection" && page.Index != context.Track(doc.ActivePage).Index)
            throw new OperationFailure("SELECTION_PAGE_MISMATCH", "Экспорт выделения требует текущую страницу документа.");
        var selection = context.Track(doc.SelectionRange);
        if (range == "selection" && selection.Count == 0) throw new OperationFailure("EMPTY_SELECTION", "В целевом документе нет выделенных объектов.");
        var temporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, ".corelmcp-" + Guid.NewGuid().ToString("N") + "." + format);
        VGCore.Document? temporaryDocument = null;
        using var privateContext=new OperationContext(context.Session,context.Request,_=>{},context.Cleanup);
        using var closeHold=context.Session.CreateScope();
        using var exportScope=context.Session.CreateScope();
        VGCore.PDFVBASettings? pdf = null;
        VGCore.pdfExportRange publishRange = default;
        string? pageRange = null;
        bool pdfCaptured = false;
        try
        {
            doc.Activate(); page.Activate();
            var exportDocument = doc;
            var exportRange = range == "selection" ? VGCore.cdrExportRange.cdrSelection : VGCore.cdrExportRange.cdrCurrentPage;
            bool curves = format is "dxf" or "plt" || (format is "ai" or "eps" && args?.TextMode == "curves");
            if (curves)
            {
                context.Stage("copying_export_geometry");
                var sources = range == "selection"
                    ? Enumerable.Range(1, selection.Count).Select(index => context.Track(selection[index])).ToArray()
                    : TopLevel(context, context.Track(page.Shapes));
                if (sources.Length == 0) throw new OperationFailure("EMPTY_EXPORT_AREA", "Нет фигур для изолированного экспорта в кривых.");
                var proofs=GeometryIsolation.Capture(context,sources);var ids=proofs.Select(p=>p.StaticId).ToArray();
                double pageWidth=page.SizeWidth,pageHeight=page.SizeHeight;
                var mm = context.App.ConvertUnits(1, doc.Unit, VGCore.cdrUnit.cdrMillimeter);
                temporaryDocument = closeHold.Track(GeometryIsolation.Clone(privateContext, doc, sources));
                var targetPage = privateContext.Track(temporaryDocument.ActivePage);
                targetPage.SetSize(pageWidth,pageHeight);
                var copies=ShapeTraversal.Resolve(privateContext,targetPage,ids);var copiedProofs=GeometryIsolation.Capture(privateContext,copies);
                for(int i=0;i<proofs.Length;i++)
                {
                    GeometryIsolation.Verify(proofs[i],copiedProofs[i],mm);
                    GeometryIsolation.ConvertText(privateContext,copies[i]);
                }
                exportDocument = temporaryDocument;
                temporaryDocument.Activate();
                exportRange = VGCore.cdrExportRange.cdrCurrentPage;
            }
            if (format == "pdf")
            {
                pdf = exportScope.Track(doc.PDFSettings); publishRange = pdf.PublishRange; pageRange = pdf.PageRange; pdfCaptured = true;
                if (range == "document") pdf.PublishRange = VGCore.pdfExportRange.pdfWholeDocument;
                else { pdf.PublishRange = VGCore.pdfExportRange.pdfPageRange; pdf.PageRange = page.Index.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                doc.PublishToPDF(temporaryPath);
            }
            else if (format is "svg" or "ai" or "eps" or "dxf" or "plt")
            {
                var type = format switch { "svg" => VGCore.cdrFilter.cdrSVG, "ai" => VGCore.cdrFilter.cdrAI, "eps" => VGCore.cdrFilter.cdrEPS, "dxf" => VGCore.cdrFilter.cdrDXF, _ => VGCore.cdrFilter.cdrPLT };
                var filter = exportScope.Track(exportDocument.ExportEx(temporaryPath, type, exportRange));
                try
                {
                    if (format == "ai") { ExportFilterTypes.Require(filter, "daedea01-0cb9-11d4-b6b4-009027575a12"); ((IAIExportOptions)filter).TextAsCurves = args?.TextMode == "curves"; }
                    if (format == "eps") { ExportFilterTypes.Require(filter, "daede301-0cb9-11d4-b6b4-009027575a12"); ((IEPSExportOptions)filter).TextAsCurves = args?.TextMode == "curves"; }
                    if (format == "plt") { ExportFilterTypes.Require(filter, "daedec01-0cb9-11d4-b6b4-009027575a12"); ((IHPGLExportOptions)filter).PlotterUnits = 1000; }
                }
                catch (InvalidCastException) { throw new OperationFailure("FILTER_UNSUPPORTED", "Фильтр не предоставляет проверенный типизированный интерфейс параметров."); }
                filter.Finish();
            }
            else
            {
                double width = range == "selection" ? selection.SizeWidth : page.SizeWidth;
                double height = range == "selection" ? selection.SizeHeight : page.SizeHeight;
                if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new OperationFailure("EMPTY_EXPORT_AREA", "Область экспорта должна иметь положительные размеры.");
                int pixelWidth, pixelHeight;
                if (maxSize is not null)
                {
                    var scale = maxSize.Value / Math.Max(width, height);
                    pixelWidth = (int)Math.Max(1, Math.Round(width * scale)); pixelHeight = (int)Math.Max(1, Math.Round(height * scale));
                }
                else
                {
                    double w = Math.Max(1, Math.Ceiling(context.App.ConvertUnits(width, doc.Unit, VGCore.cdrUnit.cdrInch) * dpi));
                    double h = Math.Max(1, Math.Ceiling(context.App.ConvertUnits(height, doc.Unit, VGCore.cdrUnit.cdrInch) * dpi));
                    if (w * h > 40000000) throw new OperationFailure("EXPORT_TOO_LARGE", "Экспорт превышает 40 мегапикселей.");
                    pixelWidth = checked((int)w); pixelHeight = checked((int)h);
                }
                var type = format switch { "jpg" => VGCore.cdrFilter.cdrJPEG, "tiff" => VGCore.cdrFilter.cdrTIFF, _ => VGCore.cdrFilter.cdrPNG };
                var imageType = args?.ColorMode == "cmyk" ? VGCore.cdrImageType.cdrCMYKColorImage : VGCore.cdrImageType.cdrRGBColorImage;
                var compression = format == "tiff" && args?.TiffCompression != "none" ? VGCore.cdrCompressionType.cdrCompressionLZW : VGCore.cdrCompressionType.cdrCompressionNone;
                var area = range == "page" ? exportScope.Track(page.BoundingBox) : null;
                var filter = exportScope.Track(exportDocument.ExportBitmap(temporaryPath, type, exportRange, imageType, pixelWidth, pixelHeight, dpi, dpi,
                    VGCore.cdrAntiAliasingType.cdrNormalAntiAliasing, false, format == "tiff" && args?.Transparent == true,
                    UseColorProfile: args?.UseDocumentProfile ?? true, Compression: compression, ExportArea: area));
                if (format == "jpg") { ExportFilterTypes.Require(filter, "daede201-0cb9-11d4-b6b4-009027575a12"); ((IJpgExportOptions)filter).Compression = args?.JpegCompression ?? 80; }
                filter.Finish();
            }
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0) throw new OperationFailure("EXPORT_NOT_CREATED", "CorelDRAW не создал непустой файл экспорта.");
            context.Stage("committing_export"); context.Mutating();
            if (File.Exists(destination)) File.Replace(temporaryPath, destination, null); else File.Move(temporaryPath, destination);
            context.Artifacts.Add(destination);
            var result=Dto.Of(("path", destination), ("bytes", new FileInfo(destination).Length), ("format", format), ("range", range));
            if(format=="svg")
            {
                using var reader=File.OpenText(destination);var fidelity=ExportFidelity.AnalyzeSvg(reader);result["fidelity"]=fidelity.Status;
                if(fidelity.EmbeddedRaster)context.Warnings.Add("SVG_EMBEDDED_RASTER: SVG содержит встроенные растровые изображения; сложные заливки могли быть растрированы CorelDRAW.");
                if(fidelity.LinkedRasterReferences.Length>0)
                {
                    var assets=new List<string>();string directory=Path.GetDirectoryName(destination)!;
                    string ownFolder=Path.GetFullPath(Path.Combine(directory,Path.GetFileNameWithoutExtension(temporaryPath)+"_Images"))+Path.DirectorySeparatorChar;
                    foreach(string reference in fidelity.LinkedRasterReferences)
                    {
                        if(reference.Length>260||reference.IndexOfAny([':', '\0', '?', '#', '*', '"', '<', '>', '|'])>=0||Path.IsPathRooted(reference))continue;
                        string asset=Path.GetFullPath(Path.Combine(directory,reference.Replace('\\',Path.DirectorySeparatorChar)));
                        if(!asset.StartsWith(ownFolder,StringComparison.OrdinalIgnoreCase)||!File.Exists(asset)||(File.GetAttributes(asset)&FileAttributes.ReparsePoint)!=0)continue;
                        WorkerArtifacts.EnsureDirectory(Path.GetDirectoryName(asset)!);assets.Add(asset);context.Artifacts.Add(asset);
                    }
                    result["linked_raster_files"]=assets.ToArray();
                    context.Warnings.Add("SVG_LINKED_RASTER: SVG ссылается на отдельные растровые файлы. Переносите SVG вместе с перечисленными linked_raster_files; полнота векторного сохранения не подтверждена.");
                    if(assets.Count!=fidelity.LinkedRasterReferences.Length)context.Warnings.Add("SVG_LINKED_ASSETS_UNCONFIRMED: часть ссылок SVG не подтверждена как собственные локальные assets экспорта.");
                }
                if(fidelity.Status=="unknown")context.Warnings.Add("SVG_FIDELITY_UNCONFIRMED: полнота векторного сохранения оформления SVG не подтверждена.");
            }
            return result;
        }
        finally
        {
            if (pdfCaptured)
            {
                context.Cleanup.Try("export.publish_range", () => pdf!.PublishRange = publishRange);
                context.Cleanup.Try("export.page_range", () => pdf!.PageRange = pageRange);
            }
            bool filtersReleased=false,released=false;
            context.Cleanup.Try("export.filters",()=>{exportScope.Dispose();filtersReleased=true;});
            context.Cleanup.Try("export.private_references",()=>{privateContext.Dispose();released=true;});
            if (temporaryDocument is not null&&released&&filtersReleased) context.Cleanup.Try("export.temporary_document", () => { temporaryDocument.Dirty = false; temporaryDocument.Close(); });
            if (File.Exists(temporaryPath)) context.Cleanup.Try("export.temporary_file", () => File.Delete(temporaryPath));
        }
    }
    private static VGCore.Shape[] TopLevel(OperationContext context, VGCore.Shapes shapes)
        => Enumerable.Range(1, shapes.Count).Select(index => context.Track(shapes[index])).ToArray();
}
