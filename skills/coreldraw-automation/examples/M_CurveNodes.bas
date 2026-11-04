Option Explicit

Public Function Main() As String
    If Application.Documents.Count = 0 Then Err.Raise 5, "Main", "Нет открытого целевого документа."
    Dim d As Document, s As Shape, c As Curve, subPath As SubPath, node As Node, seg As Segment
    Dim nodeTypes As String, segmentTypes As String
    Set d = ActiveDocument
    If Not d.ActiveLayer.Editable Then Err.Raise 5, "Main", "Активный слой заблокирован."
    Set s = d.ActiveLayer.CreateRectangle2(10, 10, 40, 25)
    s.ConvertToCurves
    Set c = s.Curve
    Set subPath = c.SubPaths(1)
    For Each node In subPath.Nodes
        If Len(nodeTypes) > 0 Then nodeTypes = nodeTypes & ","
        nodeTypes = nodeTypes & CStr(node.Type)
    Next node
    For Each seg In c.Segments
        If Len(segmentTypes) > 0 Then segmentTypes = segmentTypes & ","
        segmentTypes = segmentTypes & CStr(seg.Type)
    Next seg
    Main = "{""ok"":true,""operation"":""curve_nodes"",""static_id"":" & CStr(s.StaticID) & ",""subpaths"":" & CStr(c.SubPaths.Count) & ",""nodes"":" & CStr(subPath.Nodes.Count) & ",""segments"":" & CStr(c.Segments.Count) & ",""segment_types"":[" & segmentTypes & "],""node_types"":[" & nodeTypes & "]}"
End Function
