Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, layer As Layer, a As Shape, b As Shape, g As Shape, ownShapes As ShapeRange
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    Set layer = d.ActivePage.CreateLayer("MCP demo")
    Set a = layer.CreateRectangle2(10, 10, 15, 15): Set b = layer.CreateRectangle2(30, 10, 15, 15)
    Set ownShapes = CreateShapeRange: ownShapes.Add a: ownShapes.Add b: Set g = ownShapes.Group
    Main = "{""layer"":""MCP demo"",""group_static_id"":" & CStr(g.StaticID) & ",""members_count"":" & CStr(g.Shapes.Count) & ",""member_ids"":[" & CStr(g.Shapes(1).StaticID) & "," & CStr(g.Shapes(2).StaticID) & "]}"
End Function
