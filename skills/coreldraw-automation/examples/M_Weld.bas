Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, leftShape As Shape, rightShape As Shape, result As Shape
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set leftShape = d.ActiveLayer.CreateEllipse2(20, 20, 24, 18)
    Set rightShape = d.ActiveLayer.CreateRectangle2(32, 20, 24, 18)
    Set result = leftShape.Weld(rightShape, True, True)
    If result Is Nothing Then Err.Raise 5, "Main", "Операция Weld не вернула результат."
    Main = "{""operation"":""weld"",""source_static_id"":" & CStr(leftShape.StaticID) & ",""target_static_id"":" & CStr(rightShape.StaticID) & ",""result_static_id"":" & CStr(result.StaticID) & ",""result_width_mm"":" & JsonNumber(result.SizeWidth) & ",""result_height_mm"":" & JsonNumber(result.SizeHeight) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
