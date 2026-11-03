# CorelDRAW SDK v27

Primary documentation: https://community.coreldraw.com/sdk/api/draw/27/

Query Context7 with library `/websites/community_coreldraw_sdk_api_draw` and explicitly request version 27. The following signatures were checked during development; verify further methods before using them.

| API | Signature / purpose |
| --- | --- |
| Application.CreateDocument | `Function CreateDocument() As Document` |
| Application.OpenDocument | `Function OpenDocument(FileName As String, Optional CodePage As Integer = 0) As Document` |
| GMSProjects.Create | `Function Create(Name As String, Optional ForAllUsers As Boolean = False, Optional FileName As String) As GMSProject` |
| GMSManager.RunMacro | `Function RunMacro(ModuleName As String, MacroName As String, ByRef ParamArray Parameters() As Variant) As Variant` |
| GMSProject.Unload | `Sub Unload()` |
| Document.SaveAs | `Sub SaveAs(FileName As String, Optional Options As StructSaveAsOptions = Nothing)` |
| Document.Close | Prompts on dirty documents; set Dirty=False only when discarding is intended |
| Document.ExportEx | Returns an ExportFilter; configure it and call Finish |
| Document.ExportBitmap | Typed VBA convenience call for bitmap export; returns an ExportFilter |
| Page.BoundingBox | Returns a Rect for the whole page; pass as ExportBitmap's ExportArea to preserve page margins |
| Document.PublishToPDF | Uses Document.PDFSettings; restore changed settings afterward |
| Shape.StaticID | Shape identity stable during that shape's lifetime |
| Document.FullFileName | File path where the document is saved; may be empty for new drawings |
| Document.AddPages | `Function AddPages(NumberOfPages As Long) As Page` returns the first added page |
| Page.CreateLayer | `Function CreateLayer(LayerName As String) As Layer` |
| Shape.AddToSelection | `Sub AddToSelection()` |
| ShapeRange.Move | `Sub Move(DeltaX As Double, DeltaY As Double)` in the document unit |

CorelDRAW has no standalone compiler for its document/geometry types. The installed VBA editor and COM application are required. Compilation uses VBE Compile Project command ID 578, then verifies that it is disabled. A compiler dialog can block the desktop application on some installations.

ExportBitmap and ExportEx optional parameters proved unreliable through Windows PowerShell 5.1's late-bound COM adapter on v27.2. PNG/SVG tools therefore compile a small native VBA bridge. They remain normal typed MCP tools to the caller.
