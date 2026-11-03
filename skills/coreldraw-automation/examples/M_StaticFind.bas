Option Explicit
Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, p As Page, s As Shape, found As Shape
    Set d = ActiveDocument: d.Unit = cdrMillimeter: Set p = d.ActivePage
    If d.SelectionRange.Count <> 1 Then Err.Raise 5, "Main", "Выберите один проверенный объект."
    Set s = d.SelectionRange(1)
    If s.Page.Index <> p.Index Then Err.Raise 5, "Main", "Объект выделения находится на другой странице."
    Set found = FindStaticID(p, s.StaticID)
    If found Is Nothing Then Err.Raise 5, "Main", "StaticID не найден на странице."
    Main = "{""static_id"":" & CStr(s.StaticID) & ",""found"":true,""width_mm"":" & NumberJson(found.SizeWidth) & "}"
End Function
Private Function NumberJson(ByVal value As Double) As String
    NumberJson = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(NumberJson, 1) = "." Then NumberJson = "0" & NumberJson
    If Left$(NumberJson, 2) = "-." Then NumberJson = "-0" & Mid$(NumberJson, 2)
End Function
Private Function FindStaticID(ByVal p As Page, ByVal wanted As Long) As Shape
    Dim layer As Layer, i As Long, candidate As Shape, nested As Shape
    For Each layer In p.Layers
        For i = 1 To layer.Shapes.Count
            Set candidate = layer.Shapes(i)
            If candidate.StaticID = wanted Then Set FindStaticID = candidate: Exit Function
            If candidate.Type = cdrGroupShape Then
                Set nested = FindInGroup(candidate, wanted)
                If Not nested Is Nothing Then Set FindStaticID = nested: Exit Function
            End If
        Next i
    Next layer
End Function
Private Function FindInGroup(ByVal groupShape As Shape, ByVal wanted As Long) As Shape
    Dim child As Shape, nested As Shape
    For Each child In groupShape.Shapes
        If child.StaticID = wanted Then Set FindInGroup = child: Exit Function
        If child.Type = cdrGroupShape Then
            Set nested = FindInGroup(child, wanted)
            If Not nested Is Nothing Then Set FindInGroup = nested: Exit Function
        End If
    Next child
End Function
