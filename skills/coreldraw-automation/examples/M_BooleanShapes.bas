Option Explicit
Private Const LEFT_STATIC_ID As Long = 0
Private Const RIGHT_STATIC_ID As Long = 0
Private Const BOOLEAN_OPERATION As String = "intersect"

Public Function Main() As String
    Dim saved As ShapeRange, number As Long, message As String
    Set saved = CaptureRecipeSelection(ActiveDocument)
    RecipeActionStarted True
    On Error GoTo Failed
    Main = ApplyRecipe()
    RestoreRecipeSelection ActiveDocument, saved
    Exit Function
Failed:
    number = Err.Number: message = Err.Description
    Err.Raise number, "CorelMcpRecipe", RecipeFailure(message, ActiveDocument, saved)
End Function

Private Function ApplyRecipe() As String
    Dim d As Document, leftShape As Shape, rightShape As Shape, result As Shape
    Set d = ActiveDocument
    Set leftShape = RequireStaticShape(d, LEFT_STATIC_ID)
    Set rightShape = RequireStaticShape(d, RIGHT_STATIC_ID)
    If LEFT_STATIC_ID = RIGHT_STATIC_ID Then Err.Raise 5, "Main", "DUPLICATE_STATIC_ID"
    If Not BooleanInput(leftShape) Or Not BooleanInput(rightShape) Then Err.Raise 5, "Main", "UNSUPPORTED_SHAPE: нужны rectangle, ellipse или curve."
    If Not leftShape.Layer Is rightShape.Layer Then Err.Raise 5, "Main", "DIFFERENT_LAYERS"
    If BOOLEAN_OPERATION <> "weld" And BOOLEAN_OPERATION <> "trim" And BOOLEAN_OPERATION <> "intersect" Then Err.Raise 5, "Main", "INVALID_BOOLEAN_OPERATION"
    RecipeActionStarted False, True
    Select Case BOOLEAN_OPERATION
        Case "weld": Set result = leftShape.Weld(rightShape, True, True)
        Case "trim": Set result = leftShape.Trim(rightShape, True, True)
        Case "intersect": Set result = leftShape.Intersect(rightShape, True, True)
        Case Else: Err.Raise 5, "Main", "INVALID_BOOLEAN_OPERATION"
    End Select
    If result Is Nothing Then Err.Raise 5, "Main", "NO_BOOLEAN_RESULT: результат не создан; проверьте частичное действие."
    ApplyRecipe = "{""ok"":true,""operation"":" & JsonString(BOOLEAN_OPERATION) & ",""left_static_id"":" & CStr(leftShape.StaticID) & ",""right_static_id"":" & CStr(rightShape.StaticID) & ",""result_static_id"":" & CStr(result.StaticID) & ",""width_mm"":" & JsonNumber(DocumentUnitsToMm(result.SizeWidth, d.Unit)) & "}"
End Function

Private Function BooleanInput(ByVal s As Shape) As Boolean
    BooleanInput = s.Type = cdrRectangleShape Or s.Type = cdrEllipseShape Or s.Type = cdrCurveShape
End Function
