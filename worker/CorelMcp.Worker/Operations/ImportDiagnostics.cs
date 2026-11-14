namespace CorelMcp.Worker.Operations;

internal static class ImportDiagnostics
{
    public static object Read(OperationContext c, VGCore.Shape[] roots)
    {
        List<string> warnings=["Сходство с исходником не доказано: native-фильтр мог заменить шрифты, преобразовать текст или растрировать эффекты. Требуется визуальная проверка preview."];
        HashSet<string> families=new(StringComparer.OrdinalIgnoreCase);HashSet<int> unsupported=[];
        List<object> observedFonts=[];int texts=0,effects=0;bool complete=true;
        try
        {
            var all=roots.SelectMany(root=>new[]{root}.Concat(root.Type==VGCore.cdrShapeType.cdrGroupShape
                ?ShapeTraversal.Walk(c,c.Track(root.Shapes)).Select(item=>item.Shape):[])).ToArray();
            if(all.Length>50000)throw new OperationFailure("IMPORT_DIAGNOSTIC_LIMIT","Превышен предел диагностики импорта.");
            foreach(var shape in all)
            {
                effects+=c.Track(shape.Effects).Count;
                if((int)shape.Type is not (1 or 2 or 3 or 4 or 5 or 6 or 7))unsupported.Add((int)shape.Type);
                var clip=shape.PowerClip;if(clip is not null){c.Track(clip);complete=false;}
                if(shape.Type==VGCore.cdrShapeType.cdrTextShape)
                {
                    texts++;var family=c.Track(c.Track(shape.Text).Story).Font;
                    if(string.IsNullOrWhiteSpace(family))complete=false;else families.Add(family);
                }
            }
            HashSet<string> installed=new(StringComparer.OrdinalIgnoreCase);
            if(families.Count>0)
            {
                using var fonts=new System.Drawing.Text.InstalledFontCollection();
                foreach(var font in fonts.Families)using(font)installed.Add(font.Name);
            }
            foreach(var family in families.Order(StringComparer.OrdinalIgnoreCase))
                observedFonts.Add(Dto.Of(("family",family),("installed",installed.Contains(family)),("availability_basis","windows_font_catalog"),
                    ("status",installed.Contains(family)?"passed":"unknown")));
        }
        catch(Exception error) when(error is System.Runtime.InteropServices.ExternalException or ArgumentException or OperationFailure)
        {complete=false;warnings.Add("Полнота чтения фактических шрифтов/эффектов не подтверждена.");}
        if(!complete)warnings.Add("Смешанные шрифты, вложенный PowerClip или часть native metadata не проверены полностью.");
        if(unsupported.Count>0)warnings.Add("Импорт содержит неподдержанные типы фигур; дальнейшие операции могут быть ограничены.");
        return Dto.Of(("source_fidelity","unknown"),("font_fidelity","unknown"),("effect_fidelity","unknown"),
            ("inspection_status",complete?"passed":"unknown"),("text_shape_count",texts),("observed_fonts",observedFonts),
            ("observed_effect_count",effects),("unsupported_shape_types",unsupported.Order().ToArray()),("warnings",warnings));
    }
}
