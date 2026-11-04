Option Explicit
Private Const FIRST_STATIC_ID As Long = 0
Private Const SECOND_STATIC_ID As Long = 0
Private Const LAYER_NAME As String = "MCP recipe"

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
    Dim d As Document, layer As Layer, candidate As Layer, a As Shape, b As Shape, grouped As Shape, ownShapes As ShapeRange
    Set d = ActiveDocument
    Set a = RequireStaticShape(d, FIRST_STATIC_ID)
    Set b = RequireStaticShape(d, SECOND_STATIC_ID)
    If FIRST_STATIC_ID = SECOND_STATIC_ID Then Err.Raise 5, "Main", "DUPLICATE_STATIC_ID"
    If Not a.ParentGroup Is Nothing Or Not b.ParentGroup Is Nothing Then Err.Raise 5, "Main", "NESTED_INPUT: нужны фигуры верхнего уровня."
    For Each candidate In d.ActivePage.Layers
        If candidate.Name = LAYER_NAME Then
            If Not layer Is Nothing Then Err.Raise 5, "Main", "AMBIGUOUS_LAYER"
            Set layer = candidate
        End If
    Next candidate
    If Not layer Is Nothing Then
        If Not layer.Editable Then Err.Raise 5, "Main", "LOCKED_LAYER"
    Else
        RecipeActionStarted False, True
        Set layer = d.ActivePage.CreateLayer(LAYER_NAME)
    End If
    Set ownShapes = CreateShapeRange
    ownShapes.Add a
    ownShapes.Add b
    RecipeActionStarted False, True
    ownShapes.MoveToLayer layer
    Set grouped = ownShapes.Group
    ApplyRecipe = "{""ok"":true,""operation"":""layer_group"",""layer"":" & JsonString(layer.Name) & ",""group_static_id"":" & CStr(grouped.StaticID) & ",""member_ids"":[" & CStr(a.StaticID) & "," & CStr(b.StaticID) & "]}"
End Function
