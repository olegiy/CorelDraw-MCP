Option Explicit

' Передайте модуль в corel_execute_vba с явным document_id.
' Сервер сам управляет Undo и восстанавливает состояние приложения.
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim doc As Document, rectangle As Shape
    Set doc = ActiveDocument
    doc.Unit = cdrMillimeter
    If Not doc.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set rectangle = doc.ActiveLayer.CreateRectangle2(10, 10, 40, 25)
    rectangle.Name = "Corel MCP example"
    rectangle.Fill.ApplyUniformFill CreateRGBColor(45, 125, 230)
    rectangle.Outline.Width = 0.5
    rectangle.CreateSelection
    Main = "{""created"":true,""static_id"":" & CStr(rectangle.StaticID) & ",""width_mm"":" & JsonNumber(rectangle.SizeWidth) & ",""height_mm"":" & JsonNumber(rectangle.SizeHeight) & ",""fill_type"":" & CStr(rectangle.Fill.Type) & ",""fill_rgb"":[" & CStr(rectangle.Fill.UniformColor.RGBRed) & "," & CStr(rectangle.Fill.UniformColor.RGBGreen) & "," & CStr(rectangle.Fill.UniformColor.RGBBlue) & "],""outline_width_mm"":" & JsonNumber(rectangle.Outline.Width) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
