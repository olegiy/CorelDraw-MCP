Option Explicit

' Shared bounded helpers for recipe modules. This module intentionally has no
' Main entry point so it can be compiled together with one recipe module.

Public Function JsonNumber(ByVal value As Double) As String
    Dim result As String
    If value <> value Or Abs(value) > 1.79769313486231E+308 Then Err.Raise 5, "JsonNumber", "Число не является конечным."
    result = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(result, 1) = "." Then result = "0" & result
    If Left$(result, 2) = "-." Then result = "-0" & Mid$(result, 2)
    JsonNumber = result
End Function

Public Function JsonEscape(ByVal value As String) As String
    Dim i As Long, code As Long, ch As String, result As String
    For i = 1 To Len(value)
        ch = Mid$(value, i, 1)
        code = AscW(ch)
        If code = 34 Or code = 92 Then
            result = result & Chr$(92) & ch
        ElseIf code = 8 Then
            result = result & Chr$(92) & "b"
        ElseIf code = 9 Then
            result = result & Chr$(92) & "t"
        ElseIf code = 10 Then
            result = result & Chr$(92) & "n"
        ElseIf code = 12 Then
            result = result & Chr$(92) & "f"
        ElseIf code = 13 Then
            result = result & Chr$(92) & "r"
        ElseIf code >= 0 And code < 32 Then
            result = result & Chr$(92) & "u" & Right$("0000" & Hex$(code), 4)
        Else
            result = result & ch
        End If
    Next i
    JsonEscape = result
End Function

Public Function JsonString(ByVal value As String) As String
    JsonString = Chr$(34) & JsonEscape(value) & Chr$(34)
End Function

Public Function JsonBoolean(ByVal value As Boolean) As String
    If value Then JsonBoolean = "true" Else JsonBoolean = "false"
End Function

Public Function MmToDocumentUnits(ByVal valueMm As Double, ByVal documentUnit As cdrUnit) As Double
    MmToDocumentUnits = Application.ConvertUnits(valueMm, cdrMillimeter, documentUnit)
End Function

Public Function DocumentUnitsToMm(ByVal value As Double, ByVal documentUnit As cdrUnit) As Double
    DocumentUnitsToMm = Application.ConvertUnits(value, documentUnit, cdrMillimeter)
End Function

Public Function RecipeError(ByVal code As String, ByVal message As String, ByVal applied As Boolean) As String
    RecipeError = "{" & JsonString("ok") & ":false," & JsonString("error_code") & ":" & JsonString(code) & "," & JsonString("message") & ":" & JsonString(message) & "," & JsonString("applied") & ":" & JsonBoolean(applied) & "}"
End Function

Public Function FindStaticID(ByVal page As Page, ByVal wanted As Long, Optional ByVal maxDepth As Long = 8, Optional ByVal maxVisited As Long = 10000) As Shape
    Dim visited As Long, truncated As Boolean, layer As Layer, i As Long, candidate As Shape
    If maxDepth < 0 Or maxDepth > 32 Then Err.Raise 5, "FindStaticID", "maxDepth должен быть от 0 до 32."
    If maxVisited < 1 Or maxVisited > 50000 Then Err.Raise 5, "FindStaticID", "maxVisited должен быть от 1 до 50000."
    For Each layer In page.Layers
        For i = 1 To layer.Shapes.Count
            Set candidate = FindStaticInShape(layer.Shapes(i), wanted, 0, maxDepth, maxVisited, visited, truncated)
            If Not candidate Is Nothing Then Set FindStaticID = candidate: Exit Function
            If visited >= maxVisited Then Exit For
        Next i
        If visited >= maxVisited Then Exit For
    Next layer
    If truncated Then Err.Raise 5, "FindStaticID", "SEARCH_LIMIT: часть дерева не просмотрена."
End Function

Private Function FindStaticInShape(ByVal candidate As Shape, ByVal wanted As Long, ByVal depth As Long, ByVal maxDepth As Long, ByVal maxVisited As Long, ByRef visited As Long, ByRef truncated As Boolean) As Shape
    Dim i As Long, nested As Shape
    If visited >= maxVisited Then truncated = True: Exit Function
    visited = visited + 1
    If candidate.StaticID = wanted Then Set FindStaticInShape = candidate: Exit Function
    If candidate.Type <> cdrGroupShape Or depth >= maxDepth Then
        If candidate.Type = cdrGroupShape And candidate.Shapes.Count > 0 And depth >= maxDepth Then truncated = True
        Exit Function
    End If
    For i = 1 To candidate.Shapes.Count
        Set nested = FindStaticInShape(candidate.Shapes(i), wanted, depth + 1, maxDepth, maxVisited, visited, truncated)
        If Not nested Is Nothing Then Set FindStaticInShape = nested: Exit Function
        If visited >= maxVisited Then Exit Function
    Next i
End Function

Public Function RequireStaticShape(ByVal document As Document, ByVal wanted As Long, Optional ByVal editable As Boolean = True) As Shape
    Dim found As Shape
    If wanted <= 0 Then Err.Raise 5, "RequireStaticShape", "STATIC_ID_REQUIRED: задайте проверенный StaticID."
    Set found = FindStaticID(document.ActivePage, wanted)
    If found Is Nothing Then Err.Raise 5, "RequireStaticShape", "STATIC_ID_NOT_FOUND: объект не найден на целевой странице."
    If editable And Not found.Layer.Editable Then Err.Raise 5, "RequireStaticShape", "LOCKED_LAYER: слой объекта заблокирован."
    Set RequireStaticShape = found
End Function

Public Function RequireSingleSelection(ByVal document As Document) As Shape
    If document.SelectionRange.Count <> 1 Then Err.Raise 5, "RequireSingleSelection", "Требуется ровно одна выделенная фигура."
    Set RequireSingleSelection = document.SelectionRange(1)
End Function

Public Function CaptureRecipeSelection(ByVal document As Document) As ShapeRange
    Dim result As ShapeRange, s As Shape
    If document.SelectionRange.Count > 100 Then Err.Raise 5, "CaptureRecipeSelection", "SELECTION_LIMIT: максимум 100 фигур."
    Set result = CreateShapeRange
    For Each s In document.SelectionRange
        result.Add s
    Next s
    Set CaptureRecipeSelection = result
End Function

Public Sub RestoreRecipeSelection(ByVal document As Document, ByVal saved As ShapeRange)
    document.ClearSelection
    If saved.Count > 0 Then saved.CreateSelection
End Sub

Public Function RecipeActionStarted(Optional ByVal reset As Boolean = False, Optional ByVal mark As Boolean = False) As Boolean
    Static applied As Boolean
    If reset Then applied = False
    If mark Then applied = True
    RecipeActionStarted = applied
End Function

Public Function RecipeFailure(ByVal message As String, ByVal document As Document, ByVal saved As ShapeRange) As String
    Dim cleanupFailed As Boolean, applied As Boolean, code As String
    applied = RecipeActionStarted()
    On Error Resume Next
    RestoreRecipeSelection document, saved
    cleanupFailed = Err.Number <> 0
    On Error GoTo 0
    If applied Then code = "RECIPE_ACTION_FAILED" Else code = "RECIPE_VALIDATION_FAILED"
    RecipeFailure = "{""error_code"":" & JsonString(code) & ",""message"":" & JsonString(message) & ",""applied"":" & JsonBoolean(applied) & ",""cleanup_failed"":" & JsonBoolean(cleanupFailed) & "}"
End Function
