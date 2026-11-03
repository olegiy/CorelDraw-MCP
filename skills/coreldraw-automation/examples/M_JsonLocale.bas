Option Explicit
Public Function Main() As String
    Main = "{""value_mm"":" & JsonNumber(ToMm(1#, cdrInch)) & ",""message"":""" & JsonEscape("Кавычки " & Chr$(34) & " и перенос" & vbLf) & """}"
End Function
Private Function ToMm(ByVal value As Double, ByVal sourceUnit As cdrUnit) As Double
    ToMm = Application.ConvertUnits(value, sourceUnit, cdrMillimeter)
End Function
Private Function JsonNumber(ByVal value As Double) As String
    JsonNumber = Replace(Trim$(Str$(value)), ",", ".")
    If Left$(JsonNumber, 1) = "." Then JsonNumber = "0" & JsonNumber
    If Left$(JsonNumber, 2) = "-." Then JsonNumber = "-0" & Mid$(JsonNumber, 2)
End Function
Private Function JsonEscape(ByVal value As String) As String
    Dim i As Long, code As Long, result As String, ch As String
    For i = 1 To Len(value)
        ch = Mid$(value, i, 1): code = AscW(ch)
        If code = 34 Or code = 92 Then
            result = result & Chr$(92) & ch
        ElseIf code >= 0 And code < 32 Then
            result = result & Chr$(92) & "u" & Right$("0000" & Hex$(code), 4)
        Else
            result = result & ch
        End If
    Next i
    JsonEscape = result
End Function
