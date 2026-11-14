namespace CorelMcp.Worker.Operations;

internal static class Text
{
    public static Action PrepareCreate(DrawingTarget target, CreateTextCommand args)
    {
        var c = target.Context; var layer = target.Layer(args.LayerId);
        var installed = InstalledFont(c, args.FontFamily);
        return () =>
        {
            c.Mutating();
            var shape = c.Track(args switch
            {
                ArtisticTextCommand a => layer.CreateArtisticTextWide(target.X(a.XMm), target.Y(a.YMm), a.Text, Font: installed, Size: (float)a.FontSizePt),
                ParagraphTextCommand p => layer.CreateParagraphTextWide(target.X(p.XMm), target.Y(p.YMm + p.HeightMm), target.X(p.XMm + p.WidthMm), target.Y(p.YMm), p.Text, Font: installed, Size: (float)p.FontSizePt),
                _ => throw new OperationFailure("INVALID_INPUT", "Неподдерживаемый тип текста.")
            });
            target.Observe([], shape);
            var story = c.Track(c.Track(shape.Text).Story);
            if (!string.Equals(story.Font, installed, StringComparison.OrdinalIgnoreCase))
                throw new OperationFailure("FONT_SUBSTITUTED", "CorelDRAW подставил другой шрифт; созданный StaticID сохранён в результате, повтор запрещён.", true);
        };
    }
    private static string InstalledFont(OperationContext c, string family)
    {
        var fonts = c.Track(c.App.FontList); int count = fonts.Count;
        if (count > 50000) throw new OperationFailure("FONT_CATALOG_LIMIT", "Полнота каталога шрифтов не подтверждена: превышен предел обхода.");
        for (int index = 1; index <= count; index++)
        {
            var candidate = fonts[index]; if (string.Equals(candidate, family, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new OperationFailure("FONT_NOT_INSTALLED", "Шрифт не установлен в CorelDRAW: " + family);
    }
    internal static List<int> Occurrences(string text, string query, bool caseSensitive)
    {
        List<int> result = []; int offset = 0;
        while (offset <= text.Length - query.Length)
        {
            int match = text.IndexOf(query, offset, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (match < 0) break; result.Add(match); offset = match + query.Length;
        }
        return result;
    }
    public static Action PrepareEdit(DrawingTarget target, EditTextCommand args)
    {
        var c = target.Context; var shapes = target.Resolve(args.StaticIds, false);
        if (shapes.Any(shape => shape.Type != VGCore.cdrShapeType.cdrTextShape)) throw new OperationFailure("NOT_TEXT", "Все StaticID должны быть текстовыми фигурами.");
        var font = args is FormatTextCommand { FontFamily: not null } formatting ? InstalledFont(c, formatting.FontFamily) : null;
        List<Action> actions = [];
        foreach (var shape in shapes)
        {
            var story = c.Track(c.Track(shape.Text).Story); var original = story.WideText;
            try { Validation.CorelText.Validate(original); }
            catch (Validation.TextValidationException) { throw new OperationFailure("TEXT_SOURCE_UNSUPPORTED", "Исходный текст содержит неподдерживаемые символы; правка не выполнена."); }
            if (args is ReplaceLiteralCommand replace)
            {
                var matches = Occurrences(original, replace.Old, replace.CaseSensitive);
                if ((long)original.Length + (long)matches.Count * (replace.New.Length - replace.Old.Length) > 100000)
                    throw new OperationFailure("TEXT_LIMIT_EXCEEDED", "Результат замены превышает 100000 UTF-16.");
                var chars = c.Track(story.Characters);
                foreach (int offset in matches.AsEnumerable().Reverse())
                {
                    var expected = original.Substring(offset, replace.Old.Length);
                    var range = c.Track(chars[offset + 1, replace.Old.Length]);
                    if (range.WideText != expected) throw new OperationFailure("TEXT_INDEX_UNSUPPORTED", "Нативные индексы текста не соответствуют UTF-16; замена не выполнена.");
                    actions.Add(() =>
                    {
                        if (range.WideText != expected) throw new OperationFailure("TEXT_CHANGED", "Текст изменился после preflight; диапазон не заменён.");
                        c.Mutating(); c.Track(range.ReplaceWide(replace.New)); target.Observe([shape.StaticID], shape);
                    });
                }
            }
            else if (args is SetTextCommand set)
                actions.Add(() => { c.Mutating(); story.WideText = set.Text; target.Observe([shape.StaticID], shape); });
            else if (args is FormatTextCommand format)
            {
                void Property(Action change) => actions.Add(() => { c.Mutating(); change(); target.Observe([shape.StaticID], shape); });
                if (font is not null) Property(() => story.Font = font);
                if (format.FontSizePt is not null) Property(() => story.Size = (float)format.FontSizePt.Value);
                if (format.Bold is not null) Property(() => story.Bold = format.Bold.Value);
                if (format.Italic is not null) Property(() => story.Italic = format.Italic.Value);
                if (format.Alignment is not null) Property(() => story.Alignment = format.Alignment switch
                { "left" => VGCore.cdrAlignment.cdrLeftAlignment, "center" => VGCore.cdrAlignment.cdrCenterAlignment, "right" => VGCore.cdrAlignment.cdrRightAlignment, "justify" => VGCore.cdrAlignment.cdrFullJustifyAlignment, _ => VGCore.cdrAlignment.cdrForceJustifyAlignment });
                if (format.LineSpacingPercent is not null) Property(() => story.SetLineSpacing(VGCore.cdrLineSpacingType.cdrPercentOfPointSizeLineSpacing, (float)format.LineSpacingPercent.Value));
            }
        }
        return () =>
        {
            foreach (var action in actions) action();
            // Consolidate the bounded final output; partial errors retain the observations already collected.
            c.DrawingItems.Clear(); foreach (var shape in shapes)
            {
                c.DrawingItems.Add(target.Info(shape));
                if (font is not null && !string.Equals(c.Track(c.Track(shape.Text).Story).Font, font, StringComparison.OrdinalIgnoreCase))
                    throw new OperationFailure("FONT_SUBSTITUTED", "CorelDRAW подставил другой шрифт.", true);
            }
        };
    }
    public static object Find(OperationContext c, FindTextArguments args)
    {
        c.Stage("finding_text"); var page = c.Registry.Page(c.Target!, c.Scope, args.PageIndex);
        List<object> matches = [], unknown = []; int visited = 0, matched = 0; bool complete = true;
        try
        {
            foreach (var item in ShapeTraversal.Walk(c, c.Track(page.Shapes), maxVisited: args.MaxVisited))
            {
                visited++; if (item.Shape.Type != VGCore.cdrShapeType.cdrTextShape) continue;
                var content = c.Track(c.Track(item.Shape.Text).Story).WideText;
                try { Validation.CorelText.Validate(content); }
                catch (Validation.TextValidationException) { unknown.Add(Dto.Of(("static_id", item.Shape.StaticID), ("reason", "TEXT_SOURCE_UNSUPPORTED"))); continue; }
                var positions = Occurrences(content, args.Query, args.CaseSensitive); if (positions.Count == 0) continue;
                if (matched++ < args.Offset || matches.Count >= args.Limit) continue;
                var result = Dto.Of(("static_id", item.Shape.StaticID), ("count", positions.Count));
                if (args.IncludeText)
                {
                    int start = Math.Max(0, positions[0] - 16); result["snippet"] = content.Substring(start, Math.Min(args.SnippetLimit, content.Length - start));
                }
                matches.Add(result);
            }
        }
        catch (OperationFailure error) when (error.Code == "GEOMETRY_LIMIT_EXCEEDED") { complete = false; }
        return Dto.Of(("matches", matches), ("unknown", unknown), ("complete", complete && unknown.Count == 0), ("visited", visited),
            ("matched_shapes", matched), ("next_offset", args.Offset + matches.Count < matched ? args.Offset + matches.Count : null), ("truncated", !complete));
    }
}
