Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim s As Shape, d As Document
    Set d = ActiveDocument: d.Unit = cdrMillimeter
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set s = d.ActiveLayer.CreateArtisticText(20, 40, "Corel MCP — текст")
    s.Name = "mcp-text": s.Text.Story.Font = "Arial": s.Text.Story.Size = 18
    s.Text.Story.Text = s.Text.Story.Text & " — изменено"
    Main = "{""text"":""" & JsonEscape(s.Text.Story.Text) & """,""static_id"":" & CStr(s.StaticID) & ",""name"":""" & JsonEscape(s.Name) & """,""font"":""" & JsonEscape(s.Text.Story.Font) & """,""size_pt"":" & JsonNumber(s.Text.Story.Size) & "}"
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
Private Function JsonEscape(ByVal value As String) As String
    Dim i As Long, code As Long, result As String, ch As String
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
