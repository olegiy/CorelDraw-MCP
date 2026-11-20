Attribute VB_Name = "M_McpDuplicateDemo"
Option Explicit

Private Const MAX_COPIES As Long = 1000
Private Const GAP_MM As Double = 5

Public Sub DuplicateSelectedObject()
    Dim countText As String
    Dim result As String

    countText = InputBox("Number of additional copies (1-1000):", "MCP duplicate test", "3")
    If Len(countText) = 0 Then Exit Sub

    result = DuplicateWithCount(countText)
    If Left$(result, 3) = "OK|" Then
        MsgBox Mid$(result, 4) & " copies created.", vbInformation, "MCP duplicate test"
    Else
        MsgBox result, vbExclamation, "MCP duplicate test"
    End If
End Sub

Private Function DuplicateWithCount(ByVal countText As String) As String
    Dim doc As Document
    Dim selected As ShapeRange
    Dim original As Shape
    Dim duplicateShape As Shape
    Dim count As Long
    Dim created As Long
    Dim stepX As Double
    Dim boundsX As Double, boundsY As Double, boundsWidth As Double, boundsHeight As Double
    Dim mutationStarted As Boolean
    Dim closePending As Boolean
    Dim closeUnconfirmed As Boolean
    Dim errorNumber As Long
    Dim errorMessage As String

    On Error GoTo Failed
    countText = Trim$(countText)
    If Len(countText) = 0 Or Len(countText) > 4 Or countText Like "*[!0-9]*" Then
        DuplicateWithCount = "REJECTED|Enter a whole number from 1 to 1000."
        Exit Function
    End If
    count = CLng(countText)
    If count < 1 Or count > MAX_COPIES Then
        DuplicateWithCount = "REJECTED|Enter a whole number from 1 to 1000."
        Exit Function
    End If
    If Documents.Count = 0 Then
        DuplicateWithCount = "REJECTED|Open a document and select one object."
        Exit Function
    End If
    Set doc = ActiveDocument
    Set selected = doc.SelectionRange
    If selected.Count <> 1 Then
        DuplicateWithCount = "REJECTED|Select exactly one object or group."
        Exit Function
    End If

    Set original = selected(1)
    If original.Page.Index <> doc.ActivePage.Index Then
        DuplicateWithCount = "REJECTED|Select an object on the active page."
        Exit Function
    End If
    If original.Locked Or Not original.Layer.Editable Or original.Layer.IsSpecialLayer Then
        DuplicateWithCount = "REJECTED|Select an unlocked object on an editable drawing layer."
        Exit Function
    End If
    selected.GetBoundingBox boundsX, boundsY, boundsWidth, boundsHeight, True
    stepX = boundsWidth + Application.ConvertUnits(GAP_MM, cdrMillimeter, doc.Unit)
    mutationStarted = True
    doc.BeginCommandGroup "MCP duplicate test"
    closePending = True
    Do While created < count
        Set duplicateShape = original.Duplicate(stepX * (created + 1), 0)
        created = created + 1
    Loop
    ' Do not issue a second close if the first one errors after taking effect.
    closePending = False
    closeUnconfirmed = True
    doc.EndCommandGroup
    closeUnconfirmed = False
    DuplicateWithCount = "OK|" & created
    Exit Function

Failed:
    errorNumber = Err.Number
    errorMessage = Err.Description
    DuplicateWithCount = "Error " & errorNumber & ": " & errorMessage
    If mutationStarted Then
        DuplicateWithCount = DuplicateWithCount & vbCrLf & "Confirmed copies: " & created & ". The drawing may have changed. Check it before running the macro again."
    End If
    If closeUnconfirmed Then
        DuplicateWithCount = DuplicateWithCount & vbCrLf & "Could not confirm closing the Undo group."
    End If
    If closePending Then
        On Error Resume Next
        Err.Clear
        doc.EndCommandGroup
        If Err.Number <> 0 Then
            DuplicateWithCount = DuplicateWithCount & vbCrLf & "Could not confirm closing the Undo group: " & Err.Description
        End If
        On Error GoTo 0
    End If
End Function
