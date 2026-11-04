Option Explicit
Private Const TARGET_STATIC_ID As Long = 0
Private Const COLOR_MODE As String = "rgb"

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
    Dim d As Document, s As Shape
    Set d = ActiveDocument
    Set s = RequireStaticShape(d, TARGET_STATIC_ID)
    If COLOR_MODE <> "rgb" And COLOR_MODE <> "cmyk" Then Err.Raise 5, "Main", "INVALID_COLOR_MODE"
    RecipeActionStarted False, True
    If COLOR_MODE = "rgb" Then
        s.Fill.ApplyUniformFill CreateRGBColor(30, 130, 90)
    Else
        s.Fill.ApplyUniformFill CreateCMYKColor(70, 0, 50, 10)
    End If
    s.Outline.Width = MmToDocumentUnits(0.5, d.Unit)
    ApplyRecipe = "{""ok"":true,""operation"":""fill_outline"",""static_id"":" & CStr(s.StaticID) & ",""color_mode"":" & JsonString(COLOR_MODE) & ",""outline_width_mm"":" & JsonNumber(DocumentUnitsToMm(s.Outline.Width, d.Unit)) & "}"
End Function
