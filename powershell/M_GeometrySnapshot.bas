Option Explicit
Private Const OutputPath As String = "@@OUTPUT@@"
Private fileNumber As Integer
Private byteCount As Double
Private segmentCount As Long
Private firstObject As Boolean
Private firstSegment As Boolean
Private workDocument As Document

Public Function Main() As String
    On Error GoTo Failed
    Set workDocument = ActiveDocument
    fileNumber = FreeFile
    Open OutputPath For Binary Access Write As #fileNumber
    firstObject = True
    PutAscii "{""objects"":["
@@OBJECTS@@
    PutAscii "]}"
    Close #fileNumber
    fileNumber = 0
    Main = "{""written"":true}"
    Exit Function
Failed:
    Dim n As Long, message As String
    n = Err.Number: message = Err.Description
    On Error Resume Next
    If fileNumber <> 0 Then Close #fileNumber
    On Error GoTo 0
    Err.Raise n, "CorelMcp.Geometry", message
End Function

Private Sub PutAscii(ByVal text As String)
    Dim bytes() As Byte
    byteCount = byteCount + Len(text)
    If byteCount > 67108864# Then Err.Raise vbObjectError + 2701, , "GEOMETRY_LIMIT_EXCEEDED: файл геометрии превышает 64 МиБ."
    bytes = StrConv(text, vbFromUnicode)
    Put #fileNumber, , bytes
End Sub

Private Function Num(ByVal value As Double) As String
    ' Str$ uses dot as decimal separator independently of the Windows locale.
    Num = Trim$(Str$(Application.ConvertUnits(value, workDocument.Unit, cdrMillimeter)))
    If Left$(Num, 1) = "." Then Num = "0" & Num
    If Left$(Num, 2) = "-." Then Num = "-0" & Mid$(Num, 2)
End Function

Private Function XY(ByVal x As Double, ByVal y As Double) As String
    XY = "{""x"":" & Num(x) & ",""y"":" & Num(y) & "}"
End Function

Private Function FindRoot(ByVal id As Long) As Shape
    Set FindRoot = FindRootInCollection(workDocument.ActivePage.Shapes, id, 0)
    If FindRoot Is Nothing Then Err.Raise vbObjectError + 2702, , "GEOMETRY_SNAPSHOT_MISSING: временная копия не найдена."
End Function

Private Function FindRootInCollection(ByVal shapes As Object, ByVal id As Long, ByVal depth As Long) As Shape
    Dim s As Shape, nested As Shape
    If depth > 32 Then Err.Raise vbObjectError + 2701, , "GEOMETRY_LIMIT_EXCEEDED: превышена глубина групп."
    For Each s In shapes
        If s.StaticID = id Then Set FindRootInCollection = s: Exit Function
        If s.Type = cdrGroupShape Then
            Set nested = FindRootInCollection(s.Shapes, id, depth + 1)
            If Not nested Is Nothing Then Set FindRootInCollection = nested: Exit Function
        End If
    Next s
End Function

Private Sub WriteObject(ByVal root As Shape, ByVal sourceId As Long)
    Dim working As Shape
    ' Each requested object gets a private working copy. A request may contain
    ' both a group and its child; converting one must not change the other.
    Set working = root.Duplicate(0#, 0#)
    working.MoveToLayer workDocument.ActiveLayer
    If Not firstObject Then PutAscii ","
    firstObject = False
    PutAscii "{""static_id"":" & Trim$(Str$(sourceId)) & ",""segments"":["
    firstSegment = True
    WriteShape working, 0
    If firstSegment Then Err.Raise vbObjectError + 2703, , "UNSUPPORTED_GEOMETRY: объект не содержит измеряемой геометрии."
    PutAscii "]}"
End Sub

Private Sub WriteShape(ByVal shape As Shape, ByVal depth As Long)
    Dim child As Shape, outlineShape As Shape, segment As Segment
    Dim children As New Collection
    If depth > 32 Then Err.Raise vbObjectError + 2701, , "GEOMETRY_LIMIT_EXCEEDED: превышена глубина групп."
    If shape.Type = cdrGroupShape Then
        For Each child In shape.Shapes
            children.Add child
        Next child
        For Each child In children
            WriteShape child, depth + 1
        Next child
        Exit Sub
    End If
    ' Only the owned temporary document reaches this procedure.
    If shape.Outline.Type <> cdrNoOutline Then
        Set outlineShape = shape.Outline.ConvertToObject()
        If outlineShape Is Nothing Then Err.Raise vbObjectError + 2704, , "UNSUPPORTED_GEOMETRY: не удалось преобразовать обводку."
        WriteShape outlineShape, depth + 1
    End If
    ' Once the visible outline has been materialized, a no-fill source body
    ' contributes no additional boundary and must not be emitted again.
    If shape.Fill.Type = cdrNoFill Then Exit Sub
    If shape.Type <> cdrCurveShape Then shape.ConvertToCurves
    ' Text may become a group of curves. Visit the generated group as one object.
    If shape.Type = cdrGroupShape Then
        WriteShape shape, depth + 1
        Exit Sub
    End If
    If shape.Type <> cdrCurveShape Then Err.Raise vbObjectError + 2704, , "UNSUPPORTED_GEOMETRY: не удалось преобразовать объект в кривые."
    For Each segment In shape.Curve.Segments
        segmentCount = segmentCount + 1
        If segmentCount > 200000 Then Err.Raise vbObjectError + 2701, , "GEOMETRY_LIMIT_EXCEEDED: превышен лимит сегментов."
        If Not firstSegment Then PutAscii ","
        firstSegment = False
        If segment.Type = cdrLineSegment Then
            PutAscii "{""kind"":""line"",""p0"":" & XY(segment.StartNode.PositionX, segment.StartNode.PositionY) & ",""p1"":" & XY(segment.EndNode.PositionX, segment.EndNode.PositionY) & "}"
        ElseIf segment.Type = cdrCurveSegment Then
            PutAscii "{""kind"":""cubic"",""p0"":" & XY(segment.StartNode.PositionX, segment.StartNode.PositionY) & ",""p1"":" & XY(segment.StartingControlPointX, segment.StartingControlPointY) & ",""p2"":" & XY(segment.EndingControlPointX, segment.EndingControlPointY) & ",""p3"":" & XY(segment.EndNode.PositionX, segment.EndNode.PositionY) & "}"
        Else
            Err.Raise vbObjectError + 2704, , "UNSUPPORTED_GEOMETRY: неизвестный тип сегмента."
        End If
    Next segment
End Sub
