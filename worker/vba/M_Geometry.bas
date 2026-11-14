Option Explicit

' Only receives an owned private document and already-resolved private targets.
' C# owns publication, path generation, UTF-8 encoding and document cleanup.
Public Function Extract(ByVal privateDocument As Document, ByVal targets As ShapeRange, ByVal ids As Variant, ByVal mm As Double, ByVal nonce As String) As String
    On Error GoTo Failed
    Dim i As Long, working As Shape, objects As String, segments As String, count As Long, before As Long
    If Len(nonce) <> 32 Or nonce Like "*[!0-9a-f]*" Then Err.Raise 5, "CorelMcp", "INVALID_NONCE"
    If mm <= 0 Or targets.Count > 100 Or targets.Count < 1 Then Err.Raise 5, "CorelMcp", "GEOMETRY_LIMIT_EXCEEDED"
    For i = 1 To targets.Count
        Set working = targets(i).Duplicate(0, 0)
        working.MoveToLayer privateDocument.ActiveLayer
        segments = "": before = count
        WriteShape working, mm, segments, count, 0
        If count = before Then Err.Raise 5, "CorelMcp", "UNSUPPORTED_GEOMETRY"
        If Len(objects) > 0 Then objects = objects & ","
        objects = objects & "{""static_id"":" & M_Result.N(CLng(ids(LBound(ids) + i - 1))) & ",""segments"":[" & segments & "]}"
        If Len(objects) > 67108000 Then Err.Raise 5, "CorelMcp", "GEOMETRY_LIMIT_EXCEEDED"
        Set working = Nothing
    Next i
    Extract = "{""protocol"":1,""nonce"":" & M_Result.J(nonce) & ",""objects"":[" & objects & "]}"
    Exit Function
Failed:
    Extract = "{""protocol"":1,""nonce"":" & M_Result.J(nonce) & ",""error_code"":" & M_Result.J(Err.Description) & "}"
End Function

Private Sub WriteShape(ByVal s As Shape, ByVal mm As Double, ByRef result As String, ByRef count As Long, ByVal depth As Long)
    Dim child As Shape, stroke As Shape, segment As Segment, part As String, children As Collection
    If depth > 32 Then Err.Raise 5, "CorelMcp", "GEOMETRY_LIMIT_EXCEEDED"
    If s.Type = cdrGroupShape Then
        Set children = New Collection
        For Each child In s.Shapes
            children.Add child
        Next child
        For Each child In children
            WriteShape child, mm, result, count, depth + 1
        Next child
        Exit Sub
    End If
    If s.Outline.Type <> cdrNoOutline Then
        Set stroke = s.Outline.ConvertToObject
        WriteShape stroke, mm, result, count, depth + 1
        Set stroke = Nothing
    End If
    If s.Fill.Type = cdrNoFill Then Exit Sub
    If s.Type <> cdrCurveShape Then s.ConvertToCurves
    If s.Type = cdrGroupShape Then
        WriteShape s, mm, result, count, depth + 1
        Exit Sub
    End If
    If s.Type <> cdrCurveShape Then Err.Raise 5, "CorelMcp", "UNSUPPORTED_GEOMETRY"
    For Each segment In s.Curve.Segments
        count = count + 1
        If count > 200000 Then Err.Raise 5, "CorelMcp", "GEOMETRY_LIMIT_EXCEEDED"
        Select Case segment.Type
        Case cdrLineSegment
            part = "{""kind"":""line"",""p0"":" & Point(segment.StartNode.PositionX * mm, segment.StartNode.PositionY * mm) & ",""p1"":" & Point(segment.EndNode.PositionX * mm, segment.EndNode.PositionY * mm) & "}"
        Case cdrCurveSegment
            part = "{""kind"":""cubic"",""p0"":" & Point(segment.StartNode.PositionX * mm, segment.StartNode.PositionY * mm)
            part = part & ",""p1"":" & Point(segment.StartingControlPointX * mm, segment.StartingControlPointY * mm) & ",""p2"":" & Point(segment.EndingControlPointX * mm, segment.EndingControlPointY * mm)
            part = part & ",""p3"":" & Point(segment.EndNode.PositionX * mm, segment.EndNode.PositionY * mm) & "}"
        Case Else
            Err.Raise 5, "CorelMcp", "UNSUPPORTED_GEOMETRY"
        End Select
        If Len(result) > 0 Then result = result & ","
        result = result & part
        If Len(result) > 67108000 Then Err.Raise 5, "CorelMcp", "GEOMETRY_LIMIT_EXCEEDED"
    Next segment
End Sub

Private Function Point(ByVal x As Double, ByVal y As Double) As String
    Point = "{""x"":" & M_Result.N(x) & ",""y"":" & M_Result.N(y) & "}"
End Function
