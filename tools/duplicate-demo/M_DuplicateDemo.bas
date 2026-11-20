Option Explicit

' Parameters come from the fixed worker operation. There are no dialogs.
Public Function RunRegistered(ByVal source As Shape, ByVal count As Long, _
        ByVal stepX As Double, ByVal nonce As String) As String
    Dim copy As Shape
    Dim index As Long
    Dim ids As String

    On Error GoTo Failed
    For index = 1 To count
        Set copy = source.Duplicate(stepX * index, 0)
        ids = ids & "," & CStr(copy.StaticID)
    Next index
    RunRegistered = nonce & "|OK|0|" & Mid$(ids, 2)
    Exit Function

Failed:
    RunRegistered = nonce & "|ERROR|" & CStr(Err.Number) & "|" & Mid$(ids, 2)
End Function
