Option Explicit
Private Const TEXT_KIND As String = "artistic"

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
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "LOCKED_LAYER"
    If TEXT_KIND <> "artistic" And TEXT_KIND <> "paragraph" Then Err.Raise 5, "Main", "INVALID_TEXT_KIND"
    RecipeActionStarted False, True
    If TEXT_KIND = "artistic" Then
        Set s = d.ActiveLayer.CreateArtisticText(MmToDocumentUnits(20, d.Unit), MmToDocumentUnits(40, d.Unit), "Привет, CorelDRAW")
    Else
        Set s = d.ActiveLayer.CreateParagraphText(MmToDocumentUnits(10, d.Unit), MmToDocumentUnits(60, d.Unit), MmToDocumentUnits(90, d.Unit), MmToDocumentUnits(30, d.Unit), "Привет, CorelDRAW")
    End If
    s.Text.Story.Font = "Arial"
    s.Text.Story.Size = 18
    ApplyRecipe = "{""ok"":true,""operation"":""text_unicode"",""static_id"":" & CStr(s.StaticID) & ",""text"":" & JsonString(s.Text.Story.Text) & ",""font"":" & JsonString(s.Text.Story.Font) & ",""size_pt"":" & JsonNumber(s.Text.Story.Size) & "}"
End Function
