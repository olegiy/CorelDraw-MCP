# Сценарии VBA

Передавайте в `corel_execute_vba` весь стандартный UTF-8 модуль; не импортируйте фрагменты в установленный GMS. Сервер создаёт отдельный GMS, компилирует проект и вызывает `Main() As String`.

```vba
Option Explicit
Public Function Main() As String
    Dim doc As Document, s As Shape
    Set doc = ActiveDocument
    doc.Unit = cdrMillimeter
    Set s = doc.ActiveLayer.CreateRectangle2(10, 10, 40, 25)
    s.Fill.ApplyUniformFill CreateRGBColor(45, 125, 230)
    s.CreateSelection
    Main = "{""created"":true,""static_id"":" & CStr(s.StaticID) & "}"
End Function
```

`Main` всегда возвращает JSON, включая `{}`. Для ошибок используйте `Err.Raise`; не возвращайте успешный JSON после подавления ошибки. Числа нормализуйте к точке (`Replace(Trim$(Str$(value)), ",", ".")`), строки экранируйте как JSON. `StaticID` действителен только в жизни shape и конкретном документе.

Для `corel_build_vba` укажите `source_root` и необязательные относительные `module_paths`. Принимаются 1–100 стандартных `.bas`, суммарно до 2 МиБ; `.cls` и `.frm` запрещены. Компилируется весь проект, включая невызванные процедуры. В UTF-8 snapshot удаляются строки `Attribute VB_*`; исходные файлы не изменяются.

Обёртка сервера сохраняет настройки, включает оптимизацию, отключает события, создаёт одну Undo-группу и независимо восстанавливает состояние. Не вызывайте `SaveSettings`, `RestoreSettings`, `BeginCommandGroup` или `EndCommandGroup` в сценарии. Изменения геометрии и внешних файлов не откатываются автоматически. Ошибка оставляет диагностический GMS и snapshot.

Примеры в `examples/`:

- `M_CreateGeometry.bas`, `M_Text.bas`, `M_Style.bas` создают собственные объекты и возвращают фактические свойства.
- `M_Transforms.bas`, `M_Duplicate.bas` показывают преобразования, итоговые координаты и фактическое смещение.
- `M_LayersGroups.bas` создаёт собственный слой, ShapeRange и возвращает IDs членов без использования чужого выделения.
- `M_MoveSelection.bas` и `M_StaticFind.bas` требуют заранее осмотренного выделения; StaticFind ищет точный `StaticID` рекурсивно по странице.
- `M_Boolean.bas`, `M_Weld.bas`, `M_Trim.bas`, `M_CurveNodes.bas` выполняют операции над собственными фигурами и читают IDs, типы сегментов и результат.
- `M_JsonLocale.bas` содержит приватные функции конвертации мм и JSON locale-safe helpers.

Сигнатуры выбирайте по [API 27](api-v27.md), сверяясь с Context7. Не используйте `Evaluate`, shell, диалоги, `Application.Quit` или установленный GMS. Код VBA имеет полный доступ к CorelDRAW и ОС; отдельный GMS не является песочницей.
