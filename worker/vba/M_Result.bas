Option Explicit

Public Function J(ByVal value As String) As String
    Dim i As Long, n As Long, part As String, answer As String
    answer = Chr$(34)
    For i = 1 To Len(value)
        part = Mid$(value, i, 1): n = AscW(part)
        Select Case n
        Case 34: answer = answer & "\" & Chr$(34)
        Case 92: answer = answer & "\\"
        Case 0 To 31: answer = answer & "\u" & Right$("0000" & Hex$(n), 4)
        Case Else: answer = answer & part
        End Select
    Next i
    J = answer & Chr$(34)
End Function

Public Function N(ByVal value As Double) As String
    N = Replace$(Trim$(Str$(value)), ",", ".")
    If Left$(N, 1) = "." Then N = "0" & N
    If Left$(N, 2) = "-." Then N = "-0" & Mid$(N, 2)
End Function

Public Function ColorJson(ByVal color As Color) As String
    Select Case color.Type
    Case cdrColorRGB
        ColorJson = "{""model"":""rgb"",""r"":" & N(color.RGBRed) & ",""g"":" & N(color.RGBGreen) & ",""b"":" & N(color.RGBBlue) & "}"
    Case cdrColorCMYK
        ColorJson = "{""model"":""cmyk"",""c"":" & N(color.CMYKCyan) & ",""m"":" & N(color.CMYKMagenta) & ",""y"":" & N(color.CMYKYellow) & ",""k"":" & N(color.CMYKBlack) & "}"
    Case Else
        ColorJson = "{""native_type"":" & N(color.Type) & "}"
    End Select
End Function

Public Function ItemJson(ByVal s As Shape, ByVal key As String, ByVal left As Double, ByVal bottom As Double, ByVal factor As Double) As String
    Dim answer As String, story As TextRange
    answer = "{""item_key"":" & J(key) & ",""static_id"":" & N(s.StaticID) & ",""type"":" & N(s.Type)
    answer = answer & ",""bounds_mm"":{""left"":" & N((s.LeftX - left) / factor) & ",""bottom"":" & N((s.BottomY - bottom) / factor) & ",""width"":" & N(s.SizeWidth / factor) & ",""height"":" & N(s.SizeHeight / factor) & "}"
    answer = answer & ",""rotation_deg"":" & N(s.RotationAngle) & ",""fill_type"":" & N(s.Fill.Type) & ",""outline_type"":" & N(s.Outline.Type) & ",""outline_width_mm"":" & N(s.Outline.Width / factor)
    If s.Fill.Type = cdrUniformFill Then answer = answer & ",""fill_color"":" & ColorJson(s.Fill.UniformColor)
    If s.Outline.Type <> cdrNoOutline Then answer = answer & ",""outline_color"":" & ColorJson(s.Outline.Color)
    If s.Type = cdrTextShape Then
        Set story = s.Text.Story
        answer = answer & ",""text"":" & J(story.WideText) & ",""font_family"":" & J(story.Font) & ",""font_size_pt"":" & N(story.Size)
        answer = answer & ",""bold"":" & LCase$(CStr(story.Bold)) & ",""italic"":" & LCase$(CStr(story.Italic))
        answer = answer & ",""alignment_native"":" & N(story.Alignment) & ",""line_spacing"":" & N(story.LineSpacing) & ",""line_spacing_type"":" & N(story.LineSpacingType)
    End If
    ItemJson = answer & "}"
End Function
