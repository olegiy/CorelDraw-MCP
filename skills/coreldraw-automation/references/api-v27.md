# CorelDRAW SDK API 27

Основной источник сигнатур: [CorelDRAW SDK 27](https://community.coreldraw.com/sdk/api/draw/27/). Проверяйте запросом Context7 для `/websites/community_coreldraw_sdk_api_draw` и явно указывайте API 27.

| API | Назначение |
| --- | --- |
| `Application.CreateDocument()` | Новый документ. |
| `Application.OpenDocument(FileName, Optional CodePage)` | Открытие CDR. |
| `Document.SaveAs(FileName, Optional StructSaveAsOptions)` / `Document.Close()` | Сохранение и закрытие документа. |
| `Document.ExportEx` / `Document.ExportBitmap` | Фильтры экспорта; optional-параметры late-bound PowerShell ненадёжны, поэтому сервер использует типизированный VBA bridge. |
| `Page.BoundingBox` | Границы страницы для экспорта с полями. |
| `Page.CreateLayer(LayerName)` | Собственный слой рецепта. |
| `Layer.CreateRectangle2(x,y,width,height)` / `CreateEllipse2` / `CreateArtisticText` | Создание объектов. |
| `Shape.Duplicate(OffsetX,OffsetY)` | Дублирование со смещением. |
| `Shape.Intersect(TargetShape, LeaveSource, LeaveTarget)` | Пересечение двух объектов. |
| `Shape.Weld(TargetShape, LeaveSource, LeaveTarget)` | Объединение двух объектов; рецепты передают `True, True`, чтобы сохранить источники. |
| `Shape.Trim(TargetShape, LeaveSource, LeaveTarget)` | Вычитание/обрезка источника резаком; рецепт возвращает IDs и размеры результата. |
| `Shape.Move(dx,dy)`, `Rotate(angle)`, `SetSize(width,height)` | Преобразования. |
| `Shape.ConvertToCurves`, `Shape.Curve`, `Curve.SubPaths`, `SubPath.Nodes` | Перевод в кривые и чтение узлов. |
| `Shape.StaticID` | Стабильный ID shape в течение его жизни. Не глобальный ID документа. |
| `ShapeRange.Move(dx,dy)`, `ShapeRange.Group()` | Работа с проверенным выделением и группировка собственного диапазона. |
| `Application.CreateShapeRange()`, `ShapeRange.Add(shape)` | Диапазон без изменения пользовательского выделения. |
| `Application.CreateRGBColor(r,g,b)` / `Shape.Fill.ApplyUniformFill` | RGB-оформление. |
| `Text.Story` | Текст всех фреймов; после получения читайте `Text`. |
| `Document.PublishToPDF` и `PDFSettings` | PDF с восстановлением изменённых настроек. |
| `GMSProjects.Create`, `GMSProject.Unload`, `GMSManager.RunMacro` | Изолированный временный GMS worker-а. |

`corel_describe_api` читает typelib через `IDispatch.GetTypeInfo` и `ITypeInfo.GetContainingTypeLib`; вызовы методов, произвольные getters, expression chains и пути запрещены. Результат содержит GUID/major/minor/LCID библиотеки, типы, интерфейсы, методы, параметры, optional/default, flags, readonly и enum constants. Кеш ограничен жизнью worker-модуля.

## Ограничения измерений

`corel_measure_shapes` строит собственную копию, проверяет инвариант геометрии и измеряет плоские векторные объекты. Для кривых используется адаптивная аппроксимация с заданным `tolerance_mm`; граничная ошибка ≤ tolerance, ошибка размера ≤ 2×tolerance. Затем вычисляются convex hull и minimum-area rectangle (rotating calipers). Лимит — 200000 точек на весь запрос, tolerance не ухудшается молча. При невозможности копирования или неподдерживаемом эффекте возвращается явная ошибка.

Не поддерживаются и явно отклоняются PowerClip, bitmap, lens, transparency, live effects и другие объекты без подтверждённого геометрического инварианта. Группа измеряется как один объект; occlusion другими объектами не учитывается. Исходный документ, Dirty/selection/history и установленные GMS должны остаться без изменений.

## Экспорт

`corel_export` принимает `png`, `svg`, `pdf`, `jpg`, `tiff`, `ai`, `eps`, `dxf`, `plt`. Диапазон `document` доступен только PDF; PDF не экспортирует selection. JPG/TIFF используют DPI 1–1200, цвет RGB/CMYK и документный профиль; JPG сжатие 0–100, TIFF `lzw|none`, `transparent=false` по умолчанию. AI/EPS сохраняют текст по умолчанию или переводят его в кривые; DXF/PLT используют кривые текста и сохраняют масштаб/координаты. PNG ограничен 40 МП. Экспорт создаёт staging-файл, затем атомарно публикует его с заменой существующего назначения.

SaveAs использует `StructSaveAsOptions.Overwrite = True`. В API 27 значение по умолчанию также True; False приводит к ошибке при существующем файле. Источники: https://community.coreldraw.com/sdk/api/draw/27/p/structsaveasoptions.overwrite и https://community.coreldraw.com/sdk/api/draw/27/m/document.saveas . Атомарность нативного SaveAs не гарантируется; назначение, открытое в другом документе, по-прежнему отклоняется.
