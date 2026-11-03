Option Explicit

' Pass this module to corel_execute_vba with an explicit document_id.
' The server supplies the Undo group and restores optimization/events/settings.
Public Function Main() As String
    Dim doc As Document, rectangle As Shape
    Set doc = ActiveDocument
    doc.Unit = cdrMillimeter
    Set rectangle = doc.ActiveLayer.CreateRectangle2(10, 10, 40, 25)
    rectangle.Name = "Corel MCP example"
    rectangle.Fill.ApplyUniformFill CreateRGBColor(45, 125, 230)
    rectangle.CreateSelection
    Main = "{""created"":true,""static_id"":" & CStr(rectangle.StaticID) & "}"
End Function
