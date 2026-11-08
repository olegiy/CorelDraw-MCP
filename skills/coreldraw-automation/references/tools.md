# Инструменты MCP и отчёты заданий

Сервер возвращает `job_id` сразу после принятия вызова. Для результата вызывайте `corel_get_job(job_id, wait_seconds=20)`. Опрос читает сохранённый отчёт и не обращается к CorelDRAW. `include_preview=true` прикрепляет готовый PNG только для задания `corel_preview`. Для preview допустимы `target=page|selection` (псевдоним `range` сохраняется для совместимости); в режиме selection выделение должно быть непустым.

Все 18 инструментов доступны без разрешительного config и подтверждений.
Старый `%LOCALAPPDATA%\CorelDrawMcp\config.json` не читается, не изменяется и
не удаляется; прежний `tool_policy` остаётся только историческими данными.
Подсказки `readOnlyHint`/`destructiveHint` описывают инструмент, не разрешают
или запрещают его. Внешний MCP-клиент может иметь свои подтверждения.
Прямые абсолютные пути не требуют регистрации/allowlist. В панели контекст
и проверенный PNG передаются выбранному provider автоматически; API-ключи
не помещаются в VBA, paths, сообщения или журналы.

## Инструменты

| Инструмент | Основные параметры и результат |
| --- | --- |
| `corel_status` | Состояние CorelDRAW 27, VBA, executor и точные `document_id`; `deadline_seconds` по умолчанию 600. |
| `corel_inspect_document` | `document_id`; страница, `offset`/`limit` до 500, `recursive`, `max_depth` 1–32, `max_visited`, фильтры имени/типа/`static_ids`, `include_text`, `text_limit`. Возвращает DFS-объекты с `parent_static_id`, bounds в мм, rotation, appearance, reference point, выделение и причины усечения. |
| `corel_create_document` | `request_id`, размеры `width_mm`/`height_mm` (по умолчанию 210×297), новый `document_id`. |
| `corel_open_document` | `request_id`, абсолютный путь к `.cdr`; уже открытый файл возвращает его текущий ID. |
| `corel_save_document` | `request_id`, `document_id`, необязательный абсолютный `path`; существующее назначение заменяется автоматически. |
| `corel_close_document` | `request_id`, `document_id`, `discard_changes=false`; закрывает документ, приложение не закрывает. |
| `corel_export` | `request_id`, `document_id`, путь, `format`: `png`, `pdf`, `svg`, `jpg`, `tiff`, `ai`, `eps`, `dxf`, `plt`; `range=page|selection|document`. Документ целиком доступен только для PDF, выделение — кроме PDF. JPG допускает RGB/CMYK и сжатие 0–100; TIFF — RGB/CMYK, `lzw|none`, `transparent` и профиль документа; AI/EPS — `text_mode=preserve|curves`. PNG ограничен 40 МП. |
| `corel_preview` | `document_id`, `range=page|selection`, `max_size` 64–2400 (по умолчанию 1200); PNG сохраняется в каталоге задания. |
| `corel_build_vba` | `request_id`, `source_root`, необязательные относительные `module_paths`; компилирует стандартные UTF-8 `.bas` в отдельный GMS и не выполняет код. |
| `corel_execute_vba` | `request_id`, `document_id`, полный модуль с `Public Function Main() As String`; компилирует, выполняет и проверяет, что результат — JSON. VBA имеет полный доступ к CorelDRAW и ОС. |
| `corel_describe_api` | `kind=types|members|enum`, для `members`/`enum` — имя, `filter`, `offset`, `limit` до 200; отражение IDispatch/ITypeInfo API 27 без выражений, вызовов и getter-цепочек. |
| `corel_measure_shapes` | `document_id`, текущая или заданная страница, `static_ids` либо проверенное выделение, `tolerance_mm` 0.001–1 (по умолчанию 0.01). Возвращает размеры объектов, aggregate minimum-area rectangle и `dimension_error_mm ≤ 2*tolerance_mm`. |
| `corel_add_note` | Локальная запись `lesson|limitation|idea`, `text` до 4000 символов, необязательно `job_id`. Запись помечена `verified=false`, не исполняется и не изменяет скилл. |
| `corel_export_diagnostics` | `request_id`, `job_id`, необязательный внешний абсолютный `path`, `overwrite=false`; без path уникальный файл в соседнем с runs каталоге diagnostics. Атомарный отчёт с разрешённым списком версий, времён, статусов, applied, кодов cleanup и `diagnostic_id`. Пользовательские пути, код, текст, request ID, raw errors, result и имена исключены. |
| `corel_list_artifacts` | Локальный список заданий: `bytes`, возраст, `protected`, причины защиты, `prune_token`, `details_pruned`. COM не вызывается. |
| `corel_prune_artifacts` | `request_id`, массив `{job_id, prune_token}` до 100 элементов. Удаляет только подтверждённые внутренние артефакты; результат каждого кандидата отдельный. |
| `corel_get_job` | `job_id`, `wait_seconds` до 20, `log_lines` 0–200, `include_preview`; читает отчёт без COM. |
| `corel_guide` | Локальная справка `skill|tools|vba|api|recipes|pitfalls|install`, `offset`/`limit`; COM не вызывается. Те же тексты доступны как `coreldraw://guide/<topic>`. |

## Состояния и повтор

Изменяющим вызовам нужен уникальный `request_id`. Повтор с теми же аргументами возвращает прежнее задание; изменение аргументов отклоняется. `running` означает, что вызов ещё выполняется. `timed_out` означает, что дедлайн наблюдения истёк, но COM продолжает работу. `unknown` означает потерю владельца или результата. Для `timed_out` и `unknown` повтор мутации запрещён: сначала проверьте CorelDRAW, документ и артефакты.

Значение `succeeded` с `cleanup.completed=false` означает, что действие выполнено, но восстановление состояния завершилось ошибкой. MCP помечает такой ответ ошибочным для внимания; повторять мутацию нельзя, пока результат не проверен. В `cleanup.errors` каждая ошибка содержит шаг, код и сообщение. Основная ошибка операции сохраняется отдельно от ошибок cleanup.

Каждый отчёт содержит `run_directory`, `report_path`, `log_path`, `artifacts`, `warnings`, `stage`, `error`, `cleanup` и версии. `compiler_location` появляется только при фактически выбранном диапазоне компилятора. После очистки `details_pruned=true`, журнал и подробности недоступны.

## Артефакты и диагностика

Внутренние задания находятся в `%LOCALAPPDATA%\CorelDrawMcp\runs` либо в каталоге `CORELDRAW_MCP_RUNS`. Внешние CDR, экспорты и путь диагностики не удаляются очисткой. `corel_list_artifacts` вычисляет SHA-256 fingerprint каталога и выдаёт `prune_token`; перед удалением токен проверяется повторно. Изменившийся файл даёт `ARTIFACTS_CHANGED`.

Нельзя очищать `running`, `timed_out`, `unknown`, задания с неполным cleanup или сохранённым GMS (`PROJECT_RETAINED`). Для каждой очистки сначала атомарно записывается компактный `report.json` с identity, fingerprint, статусом, кодами ошибки и `details_pruned=true`, затем удаляются исходные внутренние файлы. Резервация задания сохраняется, поэтому request ID не запускается заново. Ссылки, junction и reparse-точки запрещены; параллельная очистка получает `PRUNE_BUSY`.

Путь диагностики должен находиться вне каталога служебных заданий. Отчёт не содержит исходный `request_id`, исходный код, пользовательские пути/имена, `result` или сырые тексты исключений. У старых отчётов без версии выводится `нет данных`.

## Пути и VBA

Все пути абсолютные Windows. Родительские каталоги должны существовать. Save As и экспорт заменяют существующий файл автоматически, без поля `overwrite`; закрытие грязного документа требует сохранения либо `discard_changes=true`. Экспорт сначала создаёт промежуточный файл и атомарно публикует его после успешного фильтра. Нативный SaveAs не обещает атомарность.

Сборка принимает 1–100 стандартных `.bas`, суммарно до 2 МиБ; пути модулей относительны `source_root`, имена ASCII и уникальны. Выполнение принимает до 256 КиБ исходника, JSON результата — до 1 МиБ. `.cls` и `.frm` не поддерживаются. UTF-8 snapshot сохраняется для диагностики; установленные GMS-проекты не изменяются.

## Проверяемые контракты

Точные публичные descriptions из реестра инструментов:

```json
{"corel_status":"Подключиться к CorelDRAW 27: версия, VBA, состояние приложения и точные ID документов. Возвращает job_id.","corel_inspect_document":"Ограниченная инспекция страницы и групп: StaticID, родитель, габариты, оформление, выделение. Текст только include_text=true. Возвращает job_id.","corel_create_document":"Создать несохранённый документ в миллиметрах и вернуть document_id через задание.","corel_open_document":"Открыть CDR и получить document_id. Возвращает job_id.","corel_save_document":"Сохранить точный CDR; существующий файл назначения заменяется автоматически. Возвращает job_id.","corel_close_document":"Закрыть точный документ; CorelDRAW не закрывается. Возвращает job_id.","corel_export":"Экспорт PNG/PDF/SVG/JPEG/TIFF/AI/EPS/DXF/PLT через промежуточный файл с автоматической заменой назначения. Возвращает job_id.","corel_preview":"PNG-превью страницы или выделения (`target=page|selection`). Получить изображение через corel_get_job.","corel_build_vba":"Полностью скомпилировать UTF-8 .bas в отдельном GMS без выполнения. Возвращает job_id.","corel_execute_vba":"Выполнить Main() As String с JSON-результатом и восстановлением настроек. VBA имеет полный доступ к системе, это не sandbox. Возвращает job_id.","corel_describe_api":"Метаданные установленного API 27; выражения и вызовы запрещены. Возвращает job_id.","corel_measure_shapes":"Измерить векторные фигуры или выделение: минимальная ширина и прямоугольник по объектам и набору. Возвращает job_id.","corel_add_note":"Локально сохранить непроверенную находку. Текст не исполняется и не меняет скилл.","corel_export_diagnostics":"Создать локальный краткий технический отчёт без кода, путей и пользовательского содержимого.","corel_list_artifacts":"Показать размеры, возраст и защищённые артефакты заданий.","corel_prune_artifacts":"Удалить выбранные внутренние артефакты после проверки prune_token; компактный отчёт и идемпотентность сохраняются."}
```

Следующие записи перечисляют обязательные входы, значения enum и значения
по умолчанию. Они проверяются `npm run check:contracts` по собранным Zod-схемам
без запуска handlers или COM. Условные требования (например name для members)
по-прежнему проверяются схемой. `target` preview вычисляется как target, затем
legacy range, затем page; отдельного schema-default для target нет.

```jsonl
{"tool":"corel_status","required":[],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_inspect_document","required":["document_id"],"defaults":{"offset":0,"limit":100,"recursive":false,"max_depth":8,"max_visited":10000,"include_text":false,"text_limit":1000,"deadline_seconds":600},"enums":{}}
{"tool":"corel_create_document","required":["request_id"],"defaults":{"width_mm":210,"height_mm":297,"deadline_seconds":600},"enums":{}}
{"tool":"corel_open_document","required":["request_id","path"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_save_document","required":["request_id","document_id"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_close_document","required":["request_id","document_id"],"defaults":{"discard_changes":false,"deadline_seconds":600},"enums":{}}
{"tool":"corel_export","required":["request_id","document_id","path","format"],"defaults":{"range":"page","dpi":150,"deadline_seconds":600},"enums":{"format":["png","pdf","svg","jpg","jpeg","tiff","tif","ai","eps","dxf","plt"],"range":["page","selection","document"],"color_mode":["rgb","cmyk"],"tiff_compression":["lzw","none"],"text_mode":["preserve","curves"]}}
{"tool":"corel_preview","required":["document_id"],"defaults":{"max_size":1200,"deadline_seconds":600},"enums":{"target":["page","selection"],"range":["page","selection"]}}
{"tool":"corel_build_vba","required":["request_id","source_root"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_execute_vba","required":["request_id","document_id","code"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_describe_api","required":["kind"],"defaults":{"filter":"","offset":0,"limit":100,"deadline_seconds":600},"enums":{"kind":["types","members","enum"]}}
{"tool":"corel_measure_shapes","required":["document_id"],"defaults":{"tolerance_mm":0.01,"deadline_seconds":600},"enums":{}}
{"tool":"corel_add_note","required":["request_id","category","text"],"defaults":{},"enums":{"category":["lesson","limitation","idea"]}}
{"tool":"corel_export_diagnostics","required":["request_id","job_id"],"defaults":{"overwrite":false},"enums":{}}
{"tool":"corel_list_artifacts","required":[],"defaults":{"offset":0,"limit":50},"enums":{}}
{"tool":"corel_prune_artifacts","required":["request_id","candidates"],"defaults":{},"enums":{}}
{"tool":"corel_get_job","required":["job_id"],"defaults":{"wait_seconds":0,"log_lines":50,"include_preview":false},"enums":{}}
{"tool":"corel_guide","required":[],"defaults":{"offset":0,"limit":8000},"enums":{"topic":["skill","tools","vba","api","recipes","pitfalls","install"]}}
```
