Attribute VB_Name = "M_McpDuplicateDemo"
Option Explicit

Private Const MAX_COPIES As Long = 1000
Private Const GAP_MM As Double = 5

Public Sub DuplicateSelectedObject()
    Dim countText As String
    Dim result As String

    countText = InputBox("Number of additional copies (1-1000):", "MCP duplicate test", "3")
    If Len(countText) = 0 Then
        WriteDemoLog "CANCELLED|No count entered"
        Exit Sub
    End If

    result = DuplicateWithCount(countText)
    If Left$(result, 3) = "OK|" Then
        MsgBox Mid$(result, 4) & " copies created.", vbInformation, "MCP duplicate test"
    Else
        MsgBox result, vbExclamation, "MCP duplicate test"
    End If
End Sub

Public Function DuplicateWithCount(ByVal countText As String) As String
    Dim doc As Document
    Dim selected As ShapeRange
    Dim original As Shape
    Dim duplicateShape As Shape
    Dim count As Long
    Dim created As Long
    Dim stepX As Double
    Dim boundsX As Double, boundsY As Double, boundsWidth As Double, boundsHeight As Double
    Dim groupOpen As Boolean
    Dim errorNumber As Long
    Dim errorMessage As String

    On Error GoTo Failed
    countText = Trim$(countText)
    If Len(countText) = 0 Or Len(countText) > 4 Or countText Like "*[!0-9]*" Then
        DuplicateWithCount = "REJECTED|Enter a whole number from 1 to 1000."
        GoTo Finished
    End If
    count = CLng(countText)
    If count < 1 Or count > MAX_COPIES Then
        DuplicateWithCount = "REJECTED|Enter a whole number from 1 to 1000."
        GoTo Finished
    End If
    If Documents.Count = 0 Then
        DuplicateWithCount = "REJECTED|Open a document and select one object."
        GoTo Finished
    End If
    Set doc = ActiveDocument
    Set selected = doc.SelectionRange
    If selected.Count <> 1 Then
        DuplicateWithCount = "REJECTED|Select exactly one object or group."
        GoTo Finished
    End If

    Set original = selected(1)
    selected.GetBoundingBox boundsX, boundsY, boundsWidth, boundsHeight, True
    stepX = boundsWidth + Application.ConvertUnits(GAP_MM, cdrMillimeter, doc.Unit)
    WriteDemoLog "START|requested=" & count
    doc.BeginCommandGroup "MCP duplicate test"
    groupOpen = True
    Do While created < count
        Set duplicateShape = original.Duplicate(stepX * (created + 1), 0)
        created = created + 1
    Loop
    doc.EndCommandGroup
    groupOpen = False
    DuplicateWithCount = "OK|" & created
    GoTo Finished

Failed:
    errorNumber = Err.Number
    errorMessage = Err.Description
    DuplicateWithCount = "ERROR|" & errorNumber & "|" & errorMessage & "|created=" & created
    If groupOpen Then
        On Error Resume Next
        Err.Clear
        doc.EndCommandGroup
        If Err.Number <> 0 Then
            DuplicateWithCount = DuplicateWithCount & "|EndCommandGroup failed: " & Err.Description
        ElseIf created > 0 Then
            doc.Undo 1
            If Err.Number = 0 Then
                DuplicateWithCount = DuplicateWithCount & "|rolled back"
            Else
                DuplicateWithCount = DuplicateWithCount & "|rollback failed: " & Err.Description
            End If
        End If
        On Error GoTo 0
    End If

Finished:
    WriteDemoLog DuplicateWithCount
End Function

Private Sub WriteDemoLog(ByVal message As String)
    Dim logPath As String
    Dim logFile As Integer
    Dim line As String

    line = Format$(Now, "yyyy-mm-dd hh:nn:ss") & " | " & message
    Debug.Print line
    On Error GoTo LogFailed
    logPath = Environ$("TEMP") & "\BarCode5-McpDuplicate.log"
    logFile = FreeFile
    Open logPath For Append As #logFile
    Print #logFile, line
    Close #logFile
    Exit Sub

LogFailed:
    Debug.Print "Log write failed: " & Err.Description
    On Error Resume Next
    If logFile <> 0 Then Close #logFile
End Sub
