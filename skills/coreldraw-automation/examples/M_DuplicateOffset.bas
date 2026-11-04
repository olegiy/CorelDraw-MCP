Option Explicit

Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, source As Shape, copy As Shape
    Set d = ActiveDocument
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set source = d.ActiveLayer.CreateEllipse2(20, 20, 12, 12)
    Set copy = source.Duplicate(MmToDocumentUnits(20, d.Unit), 0#)
    Main = "{""ok"":true,""operation"":""duplicate_offset"",""source_static_id"":" & CStr(source.StaticID) & ",""copy_static_id"":" & CStr(copy.StaticID) & ",""offset_x_mm"":" & JsonNumber(DocumentUnitsToMm(copy.LeftX - source.LeftX, d.Unit)) & ",""offset_y_mm"":" & JsonNumber(DocumentUnitsToMm(copy.BottomY - source.BottomY, d.Unit)) & "}"
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
