Option Explicit

Public Function Handshake(ByVal nonce As String) As String
    On Error GoTo Failed
    If Len(nonce) <> 32 Or nonce Like "*[!0-9a-f]*" Then Err.Raise 5, "CorelMcp", "INVALID_NONCE"
    Handshake = "{""protocol"":1,""module_version"":""0.4.0"",""source_sha256"":""" & M_BuildInfo.SourceHash & """,""nonce"":""" & nonce & """,""corel_major"":27,""operations"":[""handshake"",""validate_fonts"",""create_batch"",""update_batch"",""replace_literal"",""set_text"",""format_text"",""duplicate"",""convert_text"",""extract_geometry""]}"
    Exit Function
Failed:
    Handshake = "{""protocol"":1,""error_code"":""INVALID_HANDSHAKE""}"
End Function

