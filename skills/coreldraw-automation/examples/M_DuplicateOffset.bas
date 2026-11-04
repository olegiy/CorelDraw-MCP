Option Explicit
Private Const TARGET_STATIC_ID As Long = 0

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
    Dim d As Document, source As Shape, copy As Shape
    Set d = ActiveDocument
    Set source = RequireStaticShape(d, TARGET_STATIC_ID)
    RecipeActionStarted False, True
    Set copy = source.Duplicate(MmToDocumentUnits(20, d.Unit), 0#)
    ApplyRecipe = "{""ok"":true,""operation"":""duplicate_offset"",""source_static_id"":" & CStr(source.StaticID) & ",""copy_static_id"":" & CStr(copy.StaticID) & ",""offset_x_mm"":" & JsonNumber(DocumentUnitsToMm(copy.LeftX - source.LeftX, d.Unit)) & ",""offset_y_mm"":" & JsonNumber(DocumentUnitsToMm(copy.BottomY - source.BottomY, d.Unit)) & "}"
End Function
