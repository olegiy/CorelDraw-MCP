Option Explicit

' Перед запуском проверьте выбранные объекты в целевом документе.
' Сервер восстанавливает единицы и состояние даже при ошибке.
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim selected As ShapeRange, item As Shape
    ActiveDocument.Unit = cdrMillimeter
    Set selected = ActiveDocument.SelectionRange
    If selected.Count = 0 Then
        Err.Raise 5, "Main", "Сначала выберите объекты для перемещения."
    End If
    For Each item In selected
        If Not item.Layer.Editable Then Err.Raise 5, "Main", "Выделение содержит объект на заблокированном слое."
        If item.Page.Index <> ActiveDocument.ActivePage.Index Then Err.Raise 5, "Main", "Выделение содержит объект другой страницы."
    Next item
    selected.Move 5, 0
    Main = "{""moved_count"":" & CStr(selected.Count) & ",""dx_mm"":5}"
End Function
