# CorelDRAW SDK API 27

Сигнатуры подтверждаются Context7 `/websites/community_coreldraw_sdk_api_draw`, API 27, установленной type library и live tests. Полный проверенный набор, enum и fingerprints: [api-contract.json](../../../worker/api-contract.json). Более ранние версии API не заменяют проверку 27.

| CLR-сигнатура | SDK 27 |
| --- | --- |
| `ICorelExportFilter.Void Finish()` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/exportfilter.finish?lang=cs) |
| `IVGApplication.VGCore.Document CreateDocument()` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/application.createdocument?lang=cs) |
| `IVGCurve.VGCore.SubPath CreateSubPath(Double, Double)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/curve.createsubpath?lang=cs) |
| `IVGDocument.VGCore.Page AddPages(Int32)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/document.addpages?lang=cs) |
| `IVGLayer.VGCore.Shape CreateArtisticTextWide(Double, Double, System.String, VGCore.cdrTextLanguage, VGCore.cdrTextCharSet, System.String, Single, VGCore.cdrTriState, VGCore.cdrTriState, VGCore.cdrFontLine, VGCore.cdrAlignment)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/layer.createartistictextwide?lang=cs) |
| `IVGSegment.VGCore.Node AddNodeAt(Double, VGCore.cdrSegmentOffsetType)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/segment.addnodeat?lang=cs) |
| `IVGShape.Void GetBoundingBox(Double ByRef, Double ByRef, Double ByRef, Double ByRef, Boolean)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/shape.getboundingbox?lang=cs) |
| `IVGTextRange.VGCore.TextRange ReplaceWide(System.String, VGCore.cdrTextLanguage, VGCore.cdrTextCharSet, System.String)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/textrange.replacewide?lang=cs) |
| `IVGShapeRange.Void AddToPowerClip(VGCore.Shape, VGCore.cdrTriState)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/shaperange.addtopowerclip?lang=cs) |
| `IVGPowerClip.VGCore.ShapeRange ExtractShapes()` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/powerclip.extractshapes?lang=cs) |
| `IVGLayer.VGCore.ImportFilter ImportEx(System.String, VGCore.cdrFilter, VGCore.StructImportOptions)` | [Источник](https://community.coreldraw.com/sdk/api/draw/27/m/layer.importex?lang=cs) |

Runtime использует типизированные C# вызовы и bounded DTO. Describe API возвращает только метаданные ITypeInfo; выражения, getter-цепочки и вызовы по имени не принимаются. PowerClip, фильтры и Unicode имеют отдельно проверенные ограничения из [operations.md](operations.md).
