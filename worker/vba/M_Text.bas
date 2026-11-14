Option Explicit

' Positions are ordinal UTF-16 offsets checked by C# before the command group.
' No locale-dependent search or generated executable text is accepted here.
Public Sub ApplyEdit(ByVal s As Shape, ByVal data As Variant, ByVal row As Long, ByRef started As Boolean, ByRef pending As Boolean, ByRef changed As Boolean)
    Dim story As TextRange, part As TextRange, positions As Variant, j As Long, offset As Long
    If s.Type <> cdrTextShape Then Err.Raise 5, "CorelMcp", "NOT_TEXT"
    Set story = s.Text.Story
    If StrComp(story.WideText, CStr(data(row, 35)), vbBinaryCompare) <> 0 Then Err.Raise 5, "CorelMcp", "TEXT_CHANGED"
    Select Case CLng(data(row, 27))
    Case 1
        started = True: pending = True: story.WideText = CStr(data(row, 28)): changed = True: pending = False
    Case 2
        If Len(CStr(data(row, 29))) > 0 Then
            started = True: pending = True: story.Font = CStr(data(row, 29)): changed = True: pending = False
        End If
        If CDbl(data(row, 30)) > 0 Then
            started = True: pending = True: story.Size = CSng(data(row, 30)): changed = True: pending = False
        End If
        If CLng(data(row, 31)) <> -1 Then
            started = True: pending = True: story.Bold = (CLng(data(row, 31)) = 1): changed = True: pending = False
        End If
        If CLng(data(row, 32)) <> -1 Then
            started = True: pending = True: story.Italic = (CLng(data(row, 32)) = 1): changed = True: pending = False
        End If
        If CLng(data(row, 33)) <> -1 Then
            started = True: pending = True
            Select Case CLng(data(row, 33))
            Case 0: story.Alignment = cdrLeftAlignment
            Case 1: story.Alignment = cdrCenterAlignment
            Case 2: story.Alignment = cdrRightAlignment
            Case 3: story.Alignment = cdrFullJustifyAlignment
            Case 4: story.Alignment = cdrForceJustifyAlignment
            End Select
            changed = True: pending = False
        End If
        If CDbl(data(row, 34)) > 0 Then
            started = True: pending = True: story.SetLineSpacing cdrPercentOfPointSizeLineSpacing, CSng(data(row, 34)): changed = True: pending = False
        End If
    Case 3
        positions = data(row, 36)
        For j = CLng(data(row, 37)) - 1 To 0 Step -1
            offset = CLng(positions(j))
            Set part = story.Characters(offset + 1, CLng(data(row, 38)))
            If StrComp(part.WideText, Mid$(CStr(data(row, 35)), offset + 1, CLng(data(row, 38))), vbBinaryCompare) <> 0 Then Err.Raise 5, "CorelMcp", "TEXT_CHANGED"
            started = True: pending = True: Set part = part.ReplaceWide(CStr(data(row, 28))): changed = True: pending = False
        Next j
    Case Else
        Err.Raise 5, "CorelMcp", "INVALID_TEXT_OPERATION"
    End Select
End Sub
