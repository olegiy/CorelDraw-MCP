Option Explicit

Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, leftShape As Shape, rightShape As Shape, result As Shape
    Set d = ActiveDocument
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set leftShape = d.ActiveLayer.CreateEllipse2(20, 20, 30, 20)
    Set rightShape = d.ActiveLayer.CreateRectangle2(35, 20, 30, 20)
    Set result = leftShape.Intersect(rightShape, True, True)
    If result Is Nothing Then Err.Raise 5, "Main", "Булева операция не создала результат."
    Main = "{""ok"":true,""operation"":""intersect"",""left_static_id"":" & CStr(leftShape.StaticID) & ",""right_static_id"":" & CStr(rightShape.StaticID) & ",""result_static_id"":" & CStr(result.StaticID) & ",""width_mm"":" & JsonNumber(DocumentUnitsToMm(result.SizeWidth, d.Unit)) & "}"
End Function

Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function

Private Function DocumentUnitsToMm(ByVal value As Double, ByVal documentUnit As cdrUnit) As Double
    DocumentUnitsToMm = Application.ConvertUnits(value, documentUnit, cdrMillimeter)
End Function
