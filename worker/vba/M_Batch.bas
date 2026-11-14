Option Explicit

Public Function ValidateFonts(ByVal names As Variant, ByVal nonce As String) As String
    On Error GoTo Failed
    Dim i As Long, j As Long, found As Boolean
    If Len(nonce) <> 32 Or nonce Like "*[!0-9a-f]*" Then Err.Raise 5, "CorelMcp", "INVALID_NONCE"
    For i = LBound(names) To UBound(names)
        found = False
        If Application.FontList.Count > 50000 Then Err.Raise 5, "CorelMcp", "FONT_CATALOG_LIMIT"
        For j = 1 To Application.FontList.Count
            If StrComp(Application.FontList(j), CStr(names(i)), vbTextCompare) = 0 Then found = True: Exit For
        Next j
        If Not found Then
            ValidateFonts = "{""nonce"":" & M_Result.J(nonce) & ",""valid"":false,""error_code"":""FONT_NOT_INSTALLED""}": Exit Function
        End If
    Next i
    ValidateFonts = "{""nonce"":" & M_Result.J(nonce) & ",""valid"":true,""error_code"":""""}"
    Exit Function
Failed:
    ValidateFonts = "{""nonce"":" & M_Result.J(nonce) & ",""valid"":false,""error_code"":""FONT_VALIDATION_FAILED""}"
End Function

' All coordinates are prepared in native units by the guarded C# preflight.
' This module owns no global Document/Shape references, settings or undo groups.
Public Function Apply(ByVal target As Document, ByVal page As Page, ByVal layer As Layer, ByVal targets As ShapeRange, ByVal mode As Long, ByVal data As Variant, ByVal nonce As String, ByVal left As Double, ByVal bottom As Double, ByVal factor As Double) As String
    On Error GoTo Failed
    Dim i As Long, s As Shape, made As String, affected As String, items As String
    Dim started As Boolean, confirmed As Boolean, errorCode As String, errorMessage As String
    Dim tree As String, sourceId As Long, item As String, treeCount As Long
    Dim pending As Boolean, rowKnown As Boolean, rowChanged As Boolean, rowId As Long, knownBefore As String
    If Len(nonce) <> 32 Or nonce Like "*[!0-9a-f]*" Then Err.Raise 5, "CorelMcp", "INVALID_NONCE"
    If mode <> 1 And mode <> 2 And mode <> 3 Then Err.Raise 5, "CorelMcp", "INVALID_OPERATION"
    If factor <= 0 Then Err.Raise 5, "CorelMcp", "INVALID_UNITS"
    If target.ActivePage.TreeNode.Handle <> page.TreeNode.Handle Then Err.Raise 5, "CorelMcp", "PAGE_CHANGED"
    For i = LBound(data, 1) To UBound(data, 1)
        pending = False: rowKnown = False: rowChanged = False: rowId = 0
        If mode = 1 Then
            If CLng(data(i, 1)) = 5 Then
                Set s = targets.Item(CLng(data(i, 39)))
                If s.StaticID <> CLng(data(i, 26)) Then Err.Raise 5, "CorelMcp", "TARGET_CHANGED"
                If s.Locked Or Not s.Layer.Editable Then Err.Raise 5, "CorelMcp", "LOCKED_TARGET"
            End If
            started = True: pending = True
            Select Case CLng(data(i, 1))
            Case 1: Set s = layer.CreateRectangle2(data(i, 2), data(i, 3), data(i, 4), data(i, 5))
            Case 2: Set s = layer.CreateEllipse2(data(i, 2) + data(i, 4) / 2, data(i, 3) + data(i, 5) / 2, data(i, 4) / 2, data(i, 5) / 2)
            Case 3: Set s = layer.CreateLineSegment(data(i, 2), data(i, 3), data(i, 4), data(i, 5))
            Case 4: Set s = layer.CreateArtisticTextWide(data(i, 2), data(i, 3), CStr(data(i, 6)), Font:=CStr(data(i, 7)), Size:=CSng(data(i, 8)))
            Case 5: Set s = s.Duplicate(CDbl(data(i, 2)), CDbl(data(i, 3)))
            Case Else: Err.Raise 5, "CorelMcp", "INVALID_SHAPE"
            End Select
            rowId = s.StaticID
            If Len(made) > 0 Then made = made & ","
            made = made & M_Result.N(rowId)
            MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
            pending = False
        Else
            Set s = targets.Item(i - LBound(data, 1) + 1)
            rowId = s.StaticID
            If rowId <> CLng(data(i, 26)) Then Err.Raise 5, "CorelMcp", "TARGET_CHANGED"
            If s.Locked Or Not s.Layer.Editable Then Err.Raise 5, "CorelMcp", "LOCKED_TARGET"
        End If
        If CLng(data(i, 10)) <> 0 Then
            started = True: pending = True
            Select Case CLng(data(i, 10))
            Case 1: s.Fill.ApplyNoFill
            Case 2: s.Fill.ApplyUniformFill Application.CreateRGBColor(data(i, 11), data(i, 12), data(i, 13))
            Case 3: s.Fill.ApplyUniformFill Application.CreateCMYKColor(data(i, 11), data(i, 12), data(i, 13), data(i, 14))
            End Select
            MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
            pending = False
        End If
        If CLng(data(i, 15)) <> 0 Then
            started = True: pending = True
            Select Case CLng(data(i, 15))
            Case 1: s.Outline.SetNoOutline
            Case 2: s.Outline.SetProperties Width:=CDbl(data(i, 20)), Color:=Application.CreateRGBColor(data(i, 16), data(i, 17), data(i, 18))
            Case 3: s.Outline.SetProperties Width:=CDbl(data(i, 20)), Color:=Application.CreateCMYKColor(data(i, 16), data(i, 17), data(i, 18), data(i, 19))
            End Select
            MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
            pending = False
        End If
        If mode = 1 And CDbl(data(i, 9)) <> 0 Then
            started = True: pending = True
            s.RotateEx CDbl(data(i, 9)), s.LeftX + s.SizeWidth / 2, s.BottomY + s.SizeHeight / 2
            MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
            pending = False
        End If
        If mode = 1 And CLng(data(i, 1)) = 4 Then
            If StrComp(s.Text.Story.Font, CStr(data(i, 7)), vbTextCompare) <> 0 Then Err.Raise 5, "CorelMcp", "FONT_SUBSTITUTED"
        End If
        If mode = 2 And CLng(data(i, 21)) <> 0 Then
            started = True: pending = True
            Select Case CLng(data(i, 21))
            Case 1: s.Move CDbl(data(i, 22)), CDbl(data(i, 23))
            Case 2: s.RotateEx CDbl(data(i, 22)), CDbl(data(i, 24)), CDbl(data(i, 25))
            Case 3: s.StretchEx CDbl(data(i, 24)), CDbl(data(i, 25)), CDbl(data(i, 22)), CDbl(data(i, 23)), True
            Case 4: s.StretchEx CDbl(data(i, 24)), CDbl(data(i, 25)), -1, 1, True
            Case 5: s.StretchEx CDbl(data(i, 24)), CDbl(data(i, 25)), 1, -1, True
            End Select
            MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
            pending = False
        End If
        If mode = 2 And CLng(data(i, 27)) <> 0 Then
            M_Text.ApplyEdit s, data, i, started, pending, rowChanged
            If rowChanged Then MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
        End If
        If mode = 3 Then
            If s.Type <> cdrTextShape Then Err.Raise 5, "CorelMcp", "NOT_TEXT"
            If StrComp(s.Text.Story.WideText, CStr(data(i, 35)), vbBinaryCompare) <> 0 Then Err.Raise 5, "CorelMcp", "TEXT_CHANGED"
            sourceId = rowId
            started = True: pending = True
            s.ConvertToCurves
            AppendId knownBefore, sourceId
            confirmed = True
            tree = "": CollectConverted s, sourceId, tree, made, affected, treeCount, 0
            pending = False
            item = M_Result.ItemJson(s, CStr(data(i, 0)), left, bottom, factor)
            item = VBA.Left$(item, Len(item) - 1) & ",""source_static_id"":" & M_Result.N(sourceId) & ",""result_static_ids"":[" & tree & "]}"
        Else
            item = M_Result.ItemJson(s, CStr(data(i, 0)), left, bottom, factor)
        End If
        If Len(items) > 0 Then items = items & ","
        items = items & item
    Next i
    Apply = Reply(nonce, mode, True, confirmed, started, pending, made, affected, knownBefore, items, "", "")
    Exit Function
Failed:
    errorCode = "NATIVE_OPERATION_FAILED": errorMessage = Err.Description
    If rowChanged And rowId > 0 Then MarkKnown mode, rowId, rowKnown, confirmed, affected, knownBefore
    Apply = Reply(nonce, mode, False, confirmed, started, pending, made, affected, knownBefore, items, errorCode, errorMessage)
End Function

Private Sub CollectConverted(ByVal s As Shape, ByVal sourceId As Long, ByRef ids As String, ByRef made As String, ByRef affected As String, ByRef count As Long, ByVal depth As Long)
    Dim child As Shape, id As Long
    count = count + 1
    If count > 1000 Or depth > 32 Then Err.Raise 5, "CorelMcp", "RESULT_LIMIT_EXCEEDED"
    id = s.StaticID
    AppendId ids, id
    AppendId affected, id
    If id <> sourceId Then
        AppendId made, id
    End If
    If s.Type = cdrGroupShape Then
        For Each child In s.Shapes
            CollectConverted child, sourceId, ids, made, affected, count, depth + 1
        Next child
    End If
End Sub

Private Sub AppendId(ByRef ids As String, ByVal id As Long)
    If Len(ids) > 0 Then ids = ids & ","
    ids = ids & M_Result.N(id)
End Sub

Private Sub MarkKnown(ByVal mode As Long, ByVal id As Long, ByRef rowKnown As Boolean, ByRef confirmed As Boolean, ByRef affected As String, ByRef knownBefore As String)
    If Not rowKnown Then
        AppendId affected, id
        If mode = 2 Then AppendId knownBefore, id
        rowKnown = True
    End If
    confirmed = True
End Sub

Private Function Reply(ByVal nonce As String, ByVal mode As Long, ByVal ok As Boolean, ByVal confirmed As Boolean, ByVal started As Boolean, ByVal pending As Boolean, ByVal made As String, ByVal affected As String, ByVal knownBefore As String, ByVal items As String, ByVal code As String, ByVal message As String) As String
    Dim applied As String, operation As String
    applied = "false": If started Then applied = "null"
    If confirmed Then applied = "true"
    If pending Then applied = "null"
    operation = "update_batch": If mode = 1 Then operation = "create_batch"
    If mode = 3 Then operation = "convert_text"
    Reply = "{""protocol"":1,""nonce"":" & M_Result.J(nonce) & ",""operation"":" & M_Result.J(operation) & ",""ok"":" & LCase$(CStr(ok)) & ",""applied"":" & applied & ",""partial"":" & LCase$(CStr(Not ok And started)) & ",""created_static_ids"": [" & made & "],""affected_static_ids"": [" & affected & "],""known_before_static_ids"": [" & knownBefore & "],""items"": [" & items & "],""error_code"":" & M_Result.J(code) & ",""error_message"":" & M_Result.J(message) & "}"
End Function

