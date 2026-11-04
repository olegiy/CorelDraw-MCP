Option Explicit

Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, layer As Layer, a As Shape, b As Shape, grouped As Shape, ownShapes As ShapeRange
    Set d = ActiveDocument
    Set layer = d.ActivePage.CreateLayer("MCP recipe")
    If Not layer.Editable Then Err.Raise 5, "Main", "Созданный слой недоступен для записи."
    Set a = layer.CreateRectangle2(10, 10, 15, 15)
    Set b = layer.CreateRectangle2(30, 10, 15, 15)
    Set ownShapes = CreateShapeRange
    ownShapes.Add a
    ownShapes.Add b
    Set grouped = ownShapes.Group
    Main = "{""ok"":true,""operation"":""layer_group"",""layer"":" & JsonString(layer.Name) & ",""group_static_id"":" & CStr(grouped.StaticID) & ",""member_ids"":[" & CStr(a.StaticID) & "," & CStr(b.StaticID) & "]}"
End Function

Private Function JsonEscape(ByVal value As String) As String
    Dim i As Long, code As Long, ch As String, result As String
    For i = 1 To Len(value)
        ch = Mid$(value, i, 1): code = AscW(ch)
        If code = 34 Or code = 92 Then
            result = result & Chr$(92) & ch
        ElseIf code >= 0 And code < 32 Then
            result = result & Chr$(92) & "u" & Right$("0000" & Hex$(code), 4)
        Else
            result = result & ch
        End If
    Next i
    JsonEscape = result
End Function

Private Function JsonString(ByVal value As String) As String
    JsonString = Chr$(34) & JsonEscape(value) & Chr$(34)
End Function
