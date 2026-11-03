Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim s As Shape, d As Document
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set s = d.ActiveLayer.CreateRectangle2(10, 10, 30, 20)
    s.Rotate 15#: s.Move 5#, 3#: s.SetSize 35#, 25#
    Main = "{""rotation_deg"":" & JsonNumber(s.RotationAngle) & ",""width_mm"":" & JsonNumber(s.SizeWidth) & ",""height_mm"":" & JsonNumber(s.SizeHeight) & ",""left_mm"":" & JsonNumber(s.LeftX) & ",""bottom_mm"":" & JsonNumber(s.BottomY) & ",""static_id"":" & CStr(s.StaticID) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
