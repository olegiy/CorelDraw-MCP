Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, baseShape As Shape, cutter As Shape, result As Shape
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set baseShape = d.ActiveLayer.CreateRectangle2(20, 20, 50, 30)
    Set cutter = d.ActiveLayer.CreateEllipse2(42, 20, 20, 30)
    Set result = baseShape.Trim(cutter, True, True)
    If result Is Nothing Then Err.Raise 5, "Main", "Операция Trim не вернула результат."
    Main = "{""operation"":""trim"",""source_static_id"":" & CStr(baseShape.StaticID) & ",""target_static_id"":" & CStr(cutter.StaticID) & ",""result_static_id"":" & CStr(result.StaticID) & ",""result_width_mm"":" & JsonNumber(result.SizeWidth) & ",""result_height_mm"":" & JsonNumber(result.SizeHeight) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
