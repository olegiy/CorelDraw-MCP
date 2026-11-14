using System.Text.Json;
using CorelMcp.Worker.Interop;
using CorelMcp.Worker.Validation;

namespace CorelMcp.Worker;

internal static class LiveSmoke
{
    private const string SampleText = "Проверка CorelDRAW 27 — Ёж";

    internal sealed record Drawing(int rectangle_static_id, int text_static_id, string text, string preview_path);
    internal sealed class Report
    {
        public bool success { get; set; }
        public string? version { get; set; }
        public string runtime { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        public int sta_thread_id { get; } = Environment.CurrentManagedThreadId;
        public int created_documents { get; set; }
        public bool cleanup_completed { get; set; }
        public bool user_documents_unchanged { get; set; }
        public bool active_document_restored { get; set; }
        public bool control_document_unchanged { get; set; }
        public bool unsupported_text_rejected_before_mutation { get; set; }
        public List<Drawing> documents { get; } = [];
        public string? error { get; set; }
        public List<string> cleanup_errors { get; } = [];
        public List<string> compatibility_errors { get; } = [];
        public int user_document_count { get; set; }
    }

    private sealed record DocumentState(long identity, bool dirty, int page_count, int active_page,
        int shape_count, int unit, bool preserve_selection, string selection);

    public static Report Run(string directory)
    {
        var report = new Report();
        var scope = new ComScope();
        var connection = new CorelConnection();
        List<VGCore.Document> owned = [];
        VGCore.Application? application = null;
        VGCore.Document? previous = null;
        DocumentState[]? before = null;
        try
        {
            connection.Connect("CorelDRAW.Application.27");
            application = connection.GetApplication();
            report.version = application.Version;
            before = Snapshot(application, scope);
            report.user_document_count = before.Length;
            if (before.Length > 0) previous = scope.Track(application.ActiveDocument);
            string? controlBefore = null;
            for (var index = 0; index < 2; index++)
            {
                var document = scope.Track(application.CreateDocument());
                if (before.Any(item => item.identity == ComScope.Identity(document)))
                    throw new InvalidOperationException("CreateDocument вернул существующий документ; он не будет закрыт тестом.");
                owned.Add(document);
                report.created_documents++;
                var layer = scope.Track(document.ActiveLayer);
                var shapesBeforeValidation = scope.Track(layer.Shapes).Count;
                var dirtyBeforeValidation = document.Dirty;
                try { CorelText.Validate("🦔"); }
                catch (TextValidationException error) when (error.Code == "TEXT_UNSUPPORTED_CHARACTER")
                {
                    report.unsupported_text_rejected_before_mutation = scope.Track(layer.Shapes).Count == shapesBeforeValidation && document.Dirty == dirtyBeforeValidation;
                }
                var validatedText = CorelText.Validate(SampleText);
                bool groupOpened = false;
                try
                {
                    document.BeginCommandGroup("CorelMcp: временный тест C#");
                    groupOpened = true;
                    document.Unit = VGCore.cdrUnit.cdrMillimeter;
                    var rectangle = scope.Track(layer.CreateRectangle2(10, 10, 30, 20));
                    var rectangleId = rectangle.StaticID;
                    var text = scope.Track(layer.CreateArtisticTextWide(10, 40, validatedText.Value));
                    var textId = text.StaticID;
                    var story = scope.Track(scope.Track(text.Text).Story);
                    if (story.WideText != SampleText) report.compatibility_errors.Add("Unicode-текст не совпал после чтения: " + JsonSerializer.Serialize(story.WideText));
                    if (rectangle.StaticID != rectangleId || text.StaticID != textId)
                        report.compatibility_errors.Add("StaticID изменился при добавлении независимых фигур.");
                    if (rectangle.StaticID <= 0 || text.StaticID <= 0 || rectangle.StaticID == text.StaticID)
                        throw new InvalidOperationException("Некорректные StaticID тестовых фигур.");
                    var path = Path.Combine(directory, $"drawing-{index + 1}.png");
                    var export = scope.Track(document.ExportBitmap(path, VGCore.cdrFilter.cdrPNG,
                        VGCore.cdrExportRange.cdrCurrentPage, VGCore.cdrImageType.cdrRGBColorImage,
                        320, 240, 72, 72));
                    export.Finish();
                    if (!File.Exists(path) || new FileInfo(path).Length < 8)
                        throw new InvalidOperationException("Экспорт не создал PNG-файл.");
                    report.documents.Add(new Drawing(rectangle.StaticID, text.StaticID, story.WideText, path));
                }
                finally
                {
                    if (groupOpened)
                    {
                        try { document.EndCommandGroup(); }
                        catch (Exception error) { report.cleanup_errors.Add("EndCommandGroup: " + error.Message); }
                    }
                }
                if (report.cleanup_errors.Count > 0) break;
                if (index == 0) controlBefore = ControlFingerprint(document, scope);
                else report.control_document_unchanged = controlBefore == ControlFingerprint(owned[0], scope);
            }
            // Restore the first temporary drawing explicitly while the second still exists.
            owned[0].Activate();
            if (ComScope.Identity(scope.Track(application.ActiveDocument)) != ComScope.Identity(owned[0]))
                throw new InvalidOperationException("CorelDRAW не активировал точный временный документ.");
            report.success = report.compatibility_errors.Count == 0 && report.unsupported_text_rejected_before_mutation && report.control_document_unchanged;
        }
        catch (Exception error) { report.error = error.ToString(); }
        finally
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                try { owned[index].Dirty = false; owned[index].Close(); }
                catch (Exception error) { report.cleanup_errors.Add("Закрытие временного документа: " + error.Message); }
            }
            if (application is not null && before is not null)
            {
                try
                {
                    previous?.Activate();
                    var after = Snapshot(application, scope);
                    report.user_documents_unchanged = before.OrderBy(item => item.identity).SequenceEqual(after.OrderBy(item => item.identity));
                    report.active_document_restored = previous is null ? after.Length == 0
                        : ComScope.Identity(scope.Track(application.ActiveDocument)) == ComScope.Identity(previous);
                }
                catch (Exception error) { report.cleanup_errors.Add("Проверка исходных документов: " + error.Message); }
            }
            try { scope.Dispose(); } catch (Exception error) { report.cleanup_errors.Add(error.Message); }
            try { connection.Dispose(); } catch (Exception error) { report.cleanup_errors.Add("Освобождение подключения: " + error.Message); }
            report.cleanup_completed = report.cleanup_errors.Count == 0 && report.user_documents_unchanged && report.active_document_restored;
            report.success &= report.cleanup_completed;
        }
        return report;
    }

    private static string ControlFingerprint(VGCore.Document document, ComScope scope)
    {
        var shapes = scope.Track(scope.Track(document.ActivePage).Shapes);
        List<object> values = [];
        for (int index = 1; index <= shapes.Count; index++)
        {
            var shape = scope.Track(shapes[index]);
            values.Add(new { id = shape.StaticID, type = (int)shape.Type, x = shape.LeftX, y = shape.BottomY,
                w = shape.SizeWidth, h = shape.SizeHeight,
                text = shape.Type == VGCore.cdrShapeType.cdrTextShape ? scope.Track(scope.Track(shape.Text).Story).WideText : null });
        }
        return JsonSerializer.Serialize(new { dirty = document.Dirty, values });
    }

    private static DocumentState[] Snapshot(VGCore.Application application, ComScope scope)
    {
        var documents = scope.Track(application.Documents);
        List<DocumentState> result = [];
        for (int index = 1; index <= documents.Count; index++)
        {
            var document = scope.Track(documents[index]);
            var page = scope.Track(document.ActivePage);
            var shapes = scope.Track(page.Shapes);
            var selection = scope.Track(document.SelectionRange);
            List<int> ids = [];
            for (int s = 1; s <= selection.Count; s++) ids.Add(scope.Track(selection[s]).StaticID);
            result.Add(new DocumentState(ComScope.Identity(document), document.Dirty, scope.Track(document.Pages).Count,
                page.Index, shapes.Count, (int)document.Unit, document.PreserveSelection, string.Join(",", ids.Order())));
        }
        return result.ToArray();
    }
}
