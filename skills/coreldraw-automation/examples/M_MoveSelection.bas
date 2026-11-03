Option Explicit

' Inspect the intended document and selection before running this example.
' Units and application state are restored by the server, including on error.
Public Function Main() As String
    Dim selected As ShapeRange
    ActiveDocument.Unit = cdrMillimeter
    Set selected = ActiveDocument.SelectionRange
    If selected.Count = 0 Then
        Err.Raise 5, "Main", "Select the shapes to move first."
    End If
    selected.Move 5, 0
    Main = "{""moved_count"":" & CStr(selected.Count) & ",""dx_mm"":5}"
End Function
