# Внедрение расширений CorelDRAW MCP

Источник требований — утверждённый пользователем план из разговора 3 ноября 2026 года.
Исходная версия: 0.1.0, commit 01ebda6. Выполнить все пять выпусков.

## Непересматриваемые ограничения

- Node.js 24, Windows PowerShell 5.1 STA, CorelDRAW API 27; COM только под существующим mutex.
- Exact document_id и Shape.StaticID; polling не вызывает COM; unknown/timed_out не разрешают replay.
- Не изменять чужие документы/GMS, не вызывать Application.Quit, не писать служебные логи в stdout MCP.
- Все новые пользовательские сообщения/описания/инструкции на русском.
- Все мутации используют request_id и сохраняют результат; новые схемы совместимы со старыми вызовами.
- Интеграционные тесты создают и закрывают только собственные временные документы; диагностика сохраняется.

## Выпуск 1

- [ ] Cleanup экспорта: независимо PublishRange, PageRange, страница, активный документ, временный файл. Primary error отдельно от cleanup.errors[{step,code,message}]. Callback progress best effort. succeeded + cleanup=false -> MCP isError=true, результат сохранён, replay запрещён.
- [ ] Самодостаточные VBA-шаблоны Main() As String: оформление, текст, трансформации, дублирование, слои/группы, поиск StaticID, булевы операции, чтение кривых. Явные функции мм/JSON, без скрытого пролога и вложенного Undo.
- [ ] preview range=page|selection default page, max_size1200 64..2400. EMPTY_SELECTION и SELECTION_PAGE_MISMATCH, сохранность состояния.

## Выпуск 2

- [ ] inspect: recursive=false max_depth8(1..32) max_visited10000(100..50000), include_text=false text_limit1000(1..10000); DFS, фильтры имени/типа/ID, parent_static_id, bounds_mm, rotation, appearance, reference_point, selection_bounds. Пагинация offset/limit, раздельные truncation причины, старое поведение сохранено.
- [ ] corel_describe_api(kind types|members|enum,name,filter,offset0,limit100<=200), ITypeInfo/ITypeLib .NET interop, кеш worker session, никаких выражений/getter цепочек. Версия/GUID/type params/optional/default/enum, обычный job.
- [ ] corel_guide и resources coreldraw://guide/<topic>: skill/tools/vba/api/recipes/pitfalls/install, один источник текста; без COM, доступны во время job.
- [ ] corel_add_note(request_id, category lesson|limitation|idea,text<=4000,job_id?), атомарно root/notes, ID/время; непроверенные локальные записи не исполняются и не меняют skill.

## Выпуск 3

- [ ] corel_export jpg,tiff,ai,eps,dxf,plt; page|selection, aliases jpg/jpeg tif/tiff. Existing stage/commit/overwrite/request_id.
- [ ] jpg dpi150 RGB Compression80; tiff dpi150 RGB LZW (none/lzw); RGB/CMYK, профиль документа, tiff transparent=false, PNG unchanged, 40MP DPI1..1200.
- [ ] ai/eps text_mode preserve(default)|curves; dxf/plt text curves на собственном временном документе, масштаб/координаты сохранены. Native filter defaults для остальных параметров, никаких диалогов/подмен форматов. Unsupported -> конкретная ошибка.

## Выпуск 4

- [ ] corel_export_diagnostics(request_id,job_id,path,overwrite=false), allowlist версий/времён/статусов/кодов/cleanup. Без исходного request_id, code/text/paths/names/result/raw errors; legacy unknown version='нет данных'; без COM, атомарно.
- [ ] corel_list_artifacts: bytes/age/protected reasons/prune_token; corel_prune_artifacts(request_id,candidates[{job_id,prune_token}]). Token проверяет неизменность report/files. Не удалять незавершённые/timed_out/unknown/cleanup=false/retained GMS. Только внутренние файлы без reparse traversal; внешние CDR/exports сохраняются.
- [ ] Перед удалением тяжёлых файлов durable компактный report со всеми identity/fingerprint/outcome/error codes/cleanup, details_pruned=true. Не удалять reservation. После restart повтор request_id не запускается, конфликт обнаруживается. Без TTL/background delete.

## Выпуск 5

- [ ] corel_measure_shapes(document_id,page_index=current,static_ids?1..100, tolerance_mm=.01 range .001..1), otherwise selection. Результаты per-object и aggregate, min_width, minimum-area rectangle(width,height,angle,area,corners), units mm, погрешность boundary=tolerance dimension<=2tolerance.
- [ ] Изолированный вид фигур/кривых/текста/групп/обводок; не учитывать occlusion другими объектами. Live effects/PowerClip/bitmap/lens/transparency unsupported явно, без частичного успеха. Group=one object.
- [ ] Временный собственный документ, copy with geometry invariant, текст/примитивы->кривые, outlines->objects, extract line/cubic segments в internal file<=64MiB. Cleanup без Undo группы исходника, исходный Dirty/selection/history unchanged.
- [ ] Чистый TS: de Casteljau flattening, convex hull, rotating calipers min width/min area; 200000 point cap, no silent tolerance degradation. Если copy invariant не подтверждён — явная ошибка, не fallback на original.

## Проверка и выпуск

- [ ] TDD для поведения: целевые тесты сначала fail, затем npm test; Corel интеграция временными документами.
- [ ] Fault injection cleanup/progress, primary error preservation, partial-success MCP contract, duplicate ID/concurrency/late result.
- [ ] Examples compile/run JSON locale, selection/depth/text inspection, typelib forbidden expressions, guides w/o COM.
- [ ] Каждому формату signature/physical dimensions/state/file preservation, DXF/PLT roundtrip, geometry analytical fixtures and live source Undo invariant.
- [ ] Secrets absence diagnostics, atomic failure, protected/prune changed/restart replay/symlink/two cleaner tests.
- [ ] Обновить schemas/skill/examples/README/installer validation, документацию результатов; reviewer всего diff перед завершением.
