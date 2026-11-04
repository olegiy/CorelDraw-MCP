Option Explicit
Private Const TARGET_STATIC_ID As Long = 0

Public Function Main() As String
    Dim saved As ShapeRange, number As Long, message As String
    Set saved = CaptureRecipeSelection(ActiveDocument)
    RecipeActionStarted True
    On Error GoTo Failed
    Main = InspectRecipe()
    RestoreRecipeSelection ActiveDocument, saved
    Exit Function
Failed:
    number = Err.Number: message = Err.Description
    Err.Raise number, "CorelMcpRecipe", RecipeFailure(message, ActiveDocument, saved)
End Function

Private Function InspectRecipe() As String
    Dim d As Document, s As Shape, c As Curve, subPath As SubPath, node As Node, seg As Segment
    Dim nodeTypes As String, segmentTypes As String, nodes As Long
    Set d = ActiveDocument
    Set s = RequireStaticShape(d, TARGET_STATIC_ID, False)
    If s.Type <> cdrCurveShape Then Err.Raise 5, "Main", "UNSUPPORTED_SHAPE: исходник не преобразуется в кривые."
    Set c = s.Curve
    If c.Nodes.Count > 10000 Or c.Segments.Count > 10000 Then Err.Raise 5, "Main", "CURVE_LIMIT"
    For Each subPath In c.SubPaths
        For Each node In subPath.Nodes
            If Len(nodeTypes) > 0 Then nodeTypes = nodeTypes & ","
            nodeTypes = nodeTypes & CStr(node.Type)
            nodes = nodes + 1
        Next node
    Next subPath
    For Each seg In c.Segments
        If Len(segmentTypes) > 0 Then segmentTypes = segmentTypes & ","
        segmentTypes = segmentTypes & CStr(seg.Type)
    Next seg
    InspectRecipe = "{""ok"":true,""operation"":""curve_nodes"",""static_id"":" & CStr(s.StaticID) & ",""subpaths"":" & CStr(c.SubPaths.Count) & ",""nodes"":" & CStr(nodes) & ",""segments"":" & CStr(c.Segments.Count) & ",""segment_types"":[" & segmentTypes & "],""node_types"":[" & nodeTypes & "]}"
End Function
