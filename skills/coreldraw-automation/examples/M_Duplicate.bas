Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim source As Shape, copy As Shape, d As Document, copies As ShapeRange
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set source = d.ActiveLayer.CreateEllipse2(20, 20, 12, 12)
    Set copy = source.Duplicate(20#, 0#)
    Set copies = CreateShapeRange: copies.Add copy
    Main = "{""source_static_id"":" & CStr(source.StaticID) & ",""copy_static_ids"": [" & CStr(copy.StaticID) & "],""copies_count"":" & CStr(copies.Count) & ",""offset_x_mm"":" & JsonNumber(copy.LeftX - source.LeftX) & ",""offset_y_mm"":" & JsonNumber(copy.BottomY - source.BottomY) & ",""copy_width_mm"":" & JsonNumber(copy.SizeWidth) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
