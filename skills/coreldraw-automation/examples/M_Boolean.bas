Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, leftShape As Shape, rightShape As Shape, result As Shape
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set leftShape = d.ActiveLayer.CreateEllipse2(20, 20, 30, 20)
    Set rightShape = d.ActiveLayer.CreateRectangle2(35, 20, 30, 20)
    Set result = leftShape.Intersect(rightShape, True, True)
    If result Is Nothing Then Err.Raise 5, "Main", "Пересечение не создано."
    Main = "{""operation"":""intersect"",""result_static_id"":" & CStr(result.StaticID) & ",""width_mm"":" & NumberJson(result.SizeWidth) & "}"
End Function
Private Function NumberJson(ByVal value As Double) As String
    NumberJson = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(NumberJson, 1) = "." Then NumberJson = "0" & NumberJson
    If Left$(NumberJson, 2) = "-." Then NumberJson = "-0" & Mid$(NumberJson, 2)
End Function
