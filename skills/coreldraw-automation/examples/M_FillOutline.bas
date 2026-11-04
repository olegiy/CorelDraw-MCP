Option Explicit

Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, s As Shape
    Set d = ActiveDocument
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set s = d.ActiveLayer.CreateRectangle2(15, 15, 25, 15)
    s.Fill.ApplyUniformFill CreateRGBColor(30, 130, 90)
    s.Outline.Width = MmToDocumentUnits(0.5, d.Unit)
    Main = "{""ok"":true,""operation"":""fill_outline"",""static_id"":" & CStr(s.StaticID) & ",""fill_rgb"":[" & CStr(s.Fill.UniformColor.RGBRed) & "," & CStr(s.Fill.UniformColor.RGBGreen) & "," & CStr(s.Fill.UniformColor.RGBBlue) & "],""outline_width_mm"":" & JsonNumber(DocumentUnitsToMm(s.Outline.Width, d.Unit)) & "}"
End Function

Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function

Private Function DocumentUnitsToMm(ByVal value As Double, ByVal documentUnit As cdrUnit) As Double
    DocumentUnitsToMm = Application.ConvertUnits(value, documentUnit, cdrMillimeter)
End Function

Private Function MmToDocumentUnits(ByVal valueMm As Double, ByVal documentUnit As cdrUnit) As Double
    MmToDocumentUnits = Application.ConvertUnits(valueMm, cdrMillimeter, documentUnit)
End Function
