# Инструменты MCP и отчёты заданий

Сервер возвращает `job_id` сразу после принятия вызова. Для результата вызывайте `corel_get_job(job_id, wait_seconds=20)`. Опрос читает сохранённый отчёт и не обращается к CorelDRAW. `include_preview=true` прикрепляет готовый PNG только для задания `corel_preview`. Для preview допустимы `target=page|selection` (псевдоним `range` сохраняется для совместимости); в режиме selection выделение должно быть непустым.

Сервер публикует 39 инструментов. Единственный backend — C# STA; произвольный код не исполняется, runtime fallback отсутствует.
Инструменты доступны без разрешительного config и подтверждений.
Старый `%LOCALAPPDATA%\CorelDrawMcp\config.json` не читается, не изменяется и
не удаляется; прежний `tool_policy` остаётся только историческими данными.
Подсказки `readOnlyHint`/`destructiveHint` описывают инструмент, не разрешают
или запрещают его. Внешний MCP-клиент может иметь свои подтверждения.
Прямые абсолютные пути не требуют регистрации/allowlist. В панели контекст
и проверенный PNG передаются выбранному provider автоматически; API-ключи
не помещаются в paths, сообщения или журналы.

## Инструменты

| Инструмент | Основные параметры и результат |
| --- | --- |
| `corel_create_batch` | До 1000 фигур/надписей, уникальный item_key; требуется hybrid. Полная проверка перед изменением. |
| `corel_update_batch` | До 1000 точных StaticID, индивидуальные fill/outline/transform; требуется hybrid. |
| `corel_powerclip` | Поместить точные фигуры в контейнер PowerClip без изменения размеров или извлечь содержимое, сохраняя контейнер. Center=false по умолчанию; циклы запрещены. Возвращает job_id. |
| `corel_manage_pages` | Добавить до 100 страниц заданного размера либо изменить размер точной page_id. Удаление и перестановка не поддерживаются. Возвращает job_id. |
| `corel_import` | Импортировать один локальный SVG/PDF/PNG/JPEG до 256 МиБ по SHA-256 и размеру, на точный слой. PDF требует одну явную страницу; fit сохраняет пропорции. Без clipboard и диалогового fallback. Возвращает job_id. |
| `corel_layout_shapes` | Детерминированная shelf-раскладка bounding boxes с обводкой, без поворотов/масштабирования/копий. Dry_run по умолчанию; apply требует revision и отсутствие unplaced. Не является фигурным nesting. Возвращает job_id. |
| `corel_grid_duplicate` | Сетка по общему bounding box с обводкой; исходники образуют первую ячейку. Anchor — нижний левый угол первой ячейки в мм, до 1000 новых фигур. Возвращает job_id. |
| `corel_align_shapes` | Выровнять geometry bounds без обводки либо распределить equal gaps по оси x/y. Reference page/selection/shape; shape требует reference_id. Возвращает job_id. |
| `corel_preflight` | Проверить контуры, размеры, обводку и наличие текста без исправлений. passed/failed/unknown по каждому правилу; не гарантирует пригодность станку. Возвращает job_id. |
| `corel_find_text` | Буквальный поиск текста на точной странице без изменений. Фрагменты только include_text=true; regex запрещён. Возвращает job_id. |
| `corel_edit_text` | Буквально заменить, задать текст или формат всей истории точных текстовых фигур. Replace сохраняет форматирование за пределами совпадений; regex не поддерживается. Возвращает job_id. |
| `corel_edit_curve` | Выполнить одну правку кривой при совпадении expected_revision. Индексы с единицы; join только концов в пределах tolerance_mm. Возвращает новый revision через job_id. |
| `corel_create_curve` | Создать кривую из явных line/cubic сегментов и нескольких subpaths. Координаты в мм; до 10000 узлов. Возвращает job_id. |
| `corel_inspect_curve` | Прочитать узлы/сегменты точной кривой и полный geometry_revision. Пагинация ограничивает выдачу, а не revision. Возвращает job_id. |
| `corel_convert_to_curves` | Преобразовать точные поддержанные фигуры или текст в кривые после проверки всех целей. Bitmap и эффекты не поддерживаются. Возвращает job_id. |
| `corel_create_text` | Создать artistic или paragraph текст с явным установленным шрифтом. BMP Unicode; неподдерживаемые символы отклоняются до мутации. Возвращает job_id. |
| `corel_boolean_shapes` | Weld/trim/intersect двух фигур с явными keep_left/keep_right. Trim: LEFT вырезает RIGHT. Возвращает фактические ID через job_id. |
| `corel_group_shapes` | Сгруппировать фигуры одного слоя или разгруппировать ровно одну группу на один уровень. Возвращает job_id. |
| `corel_manage_layers` | Создать, переименовать слой, изменить его флаги или перенести точные фигуры. Существующий слой задаётся opaque layer_id. Возвращает job_id. |
| `corel_style_shapes` | Задать явную заливку и/или обводку точных StaticID. Цвет RGB/CMYK, ширина в мм; отсутствующие свойства сохраняются. Возвращает job_id. |
| `corel_duplicate_shapes` | Дублировать точные StaticID с заданным шагом в мм, сохраняя исходники. До 1000 новых фигур с учётом групп. Возвращает job_id. |
| `corel_transform_shapes` | Переместить, масштабировать, повернуть или отразить точные StaticID. Явный anchor в мм; scale только положительный. Возвращает job_id. |
| `corel_create_shapes` | Создать до 100 прямоугольников, эллипсов или линий на точном слое. Координаты в мм от нижнего левого угла страницы. Возвращает job_id. |
| `corel_status` | Состояние CorelDRAW 27, VBA, executor и точные `document_id`; `vba.temporary_projects` содержит `complete`, `idle`, `gms` (имя/путь временных проектов) и `vbe_names` для отдельного подтверждения выгрузки retained GMS в панели. Ничего не выгружает; `deadline_seconds` по умолчанию 600. |
| `corel_inspect_document` | `document_id`; страница, `offset`/`limit` до 500, `recursive`, `max_depth` 1–32, `max_visited`, фильтры имени/типа/`static_ids`, `include_text`, `text_limit`. Возвращает DFS-объекты с `parent_static_id`, bounds в мм, rotation, appearance, reference point, выделение и причины усечения. |
| `corel_create_document` | `request_id`, размеры `width_mm`/`height_mm` (по умолчанию 210×297), новый `document_id`. |
| `corel_open_document` | `request_id`, абсолютный путь к `.cdr`; уже открытый файл возвращает его текущий ID. |
| `corel_save_document` | `request_id`, `document_id`, необязательный абсолютный `path`; существующее назначение заменяется автоматически. |
| `corel_close_document` | `request_id`, `document_id`, `discard_changes=false`; закрывает документ, приложение не закрывает. |
| `corel_export` | `request_id`, `document_id`, путь, `format`: `png`, `pdf`, `svg`, `jpg`, `tiff`, `ai`, `eps`, `dxf`, `plt`; `range=page|selection|document`. Документ целиком доступен только для PDF, выделение — кроме PDF. JPG допускает RGB/CMYK и сжатие 0–100; TIFF — RGB/CMYK, `lzw|none`, `transparent` и профиль документа; AI/EPS — `text_mode=preserve|curves`. PNG ограничен 40 МП. |
| `corel_preview` | `document_id`, `range=page|selection`, `max_size` 64–2400 (по умолчанию 1200); PNG сохраняется в каталоге задания. |
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

Каждый отчёт содержит `run_directory`, `report_path`, `log_path`, `artifacts`, `warnings`, `stage`, `error`, `cleanup` и версии. `compiler_location` и прежние VBA metadata читаются только как исторические данные; новые операции их не создают.

## Артефакты и диагностика

Внутренние задания находятся в `%LOCALAPPDATA%\CorelDrawMcp\runs` либо в каталоге `CORELDRAW_MCP_RUNS`. Внешние CDR, экспорты и путь диагностики не удаляются очисткой. `corel_list_artifacts` вычисляет SHA-256 fingerprint каталога и выдаёт `prune_token`; перед удалением токен проверяется повторно. Изменившийся файл даёт `ARTIFACTS_CHANGED`.

Нельзя очищать `running`, `timed_out`, `unknown`, задания с неполным cleanup или сохранённым GMS (`PROJECT_RETAINED`). Для каждой очистки сначала атомарно записывается компактный `report.json` с identity, fingerprint, статусом, кодами ошибки и `details_pruned=true`, затем удаляются исходные внутренние файлы. Резервация задания сохраняется, поэтому request ID не запускается заново. Ссылки, junction и reparse-точки запрещены; параллельная очистка получает `PRUNE_BUSY`.

Путь диагностики должен находиться вне каталога служебных заданий. Отчёт не содержит исходный `request_id`, исходный код, пользовательские пути/имена, `result` или сырые тексты исключений. У старых отчётов без версии выводится `нет данных`.

## Пути и VBA

В development backend C# новые команды требуют `request_id`, `document_id`
и `page_index`; создание также требует `layer_id` из инспекции. Для выбора
обычного слоя используйте `is_special_layer=false`. Координаты и ширина
обводки — мм, начало координат в нижнем левом углу указанной страницы.
Масштаб sx/sy положительный; отражение — отдельный action flip с anchor.
Trim означает LEFT вырезает RIGHT, keep-флаги обязательны. Строки нового
текста ограничены BMP без NUL: emoji и одиночные surrogate отклоняются до
мутации. Отсутствующий шрифт не подменяется автоматически. Ошибка с
`applied:true` и `partial:true` содержит наблюдаемые ID; повтор запрещён.

`corel_inspect_curve` считает revision по полной геометрии до пагинации;
offset/limit применяются отдельно к плоским спискам nodes и segments.
Subpath/node/segment indices начинаются с единицы. Координаты точек и
control1/control2 — мм от страницы. Создание closed subpath добавляет
замыкающий line-сегмент; его конечную точку не дублируйте во входе.
Edit curve принимает одну action и revision предыдущей инспекции.
Insert использует параметр t, а join разрешён только для двух концов
открытых subpaths в пределах явного tolerance_mm. Delete сохраняет минимум
два узла открытого или три узла закрытого subpath. Undo group не является
транзакцией.

Replace literal меняет нативные диапазоны с конца истории и сохраняет
форматирование вне совпадений. Поиск использует ordinal case-sensitive
или ordinal case-insensitive сравнение, без regex; совпадения не
перекрываются. CorelDRAW нормализует CRLF в CR. Format применяется ко всей
истории; line_spacing_percent задаётся как процент размера шрифта в pt.
Find не выдаёт фрагменты без include_text=true. Preflight ничего не
исправляет: unknown означает неподдержанный объект, неполную проверку или
несопоставимые модели цвета. Цвета разных моделей не конвертируются через
ICC, численные допуски задаёт вызывающий. Для text_present passed означает
отсутствие текстовых фигур. Для open_contours требуются явные кривые.

Align использует geometry bounds без обводки; reference selection означает
объединение входных StaticID без обращения к выделению UI. Equal gaps
сортирует по текущему положению на оси и StaticID, распределяет фигуры
между краями reference. Grid/layout измеряют bounds с обводкой. Исходники
grid перемещаются к anchor и образуют первую ячейку; лимит 1000 новых фигур
включает потомков групп. Shelf идёт слева направо, снизу вверх в порядке
входных ID, с четырьмя margins и двумя gaps. Dry-run возвращает
placements/unplaced/layout_revision, сохраняя исходник. Apply требует тот
же набор/параметры, актуальный revision и отсутствие unplaced. Изменившиеся
контуры при прежних габаритах инвалидируют revision. Геометрия текста
читается в проверенной временной копии; исходный текст не преобразуется.
Полный snapshot ограничен 200000 узлами/сегментами, превышение отклоняется
до применения. Bounding box gaps не гарантируют минимальный зазор между
реальными контурами; это не фигурный nesting.

Все пути абсолютные Windows. Родительские каталоги должны существовать. Save As и экспорт заменяют существующий файл автоматически, без поля `overwrite`; закрытие грязного документа требует сохранения либо `discard_changes=true`. Экспорт сначала создаёт промежуточный файл и атомарно публикует его после успешного фильтра. Нативный SaveAs не обещает атомарность.

Сборка принимает 1–100 стандартных `.bas`, суммарно до 2 МиБ; пути модулей относительны `source_root`, имена ASCII и уникальны. Выполнение принимает до 256 КиБ исходника, JSON результата — до 1 МиБ. `.cls` и `.frm` не поддерживаются. UTF-8 snapshot сохраняется для диагностики; установленные GMS-проекты не изменяются.

## Проверяемые контракты

Точные публичные descriptions из реестра инструментов:

```json
{
  "corel_import": "Импортировать один локальный SVG/PDF/PNG/JPEG до 256 МиБ по SHA-256 и размеру, на точный слой. PDF требует одну явную страницу; fit сохраняет пропорции. Без clipboard и диалогового fallback. Возвращает job_id.",
  "corel_manage_pages": "Добавить до 100 страниц заданного размера либо изменить размер точной page_id. Удаление и перестановка не поддерживаются. Возвращает job_id.",
  "corel_powerclip": "Поместить точные фигуры в контейнер PowerClip без изменения размеров или извлечь содержимое, сохраняя контейнер. Center=false по умолчанию; циклы запрещены. Возвращает job_id.",
  "corel_align_shapes": "Выровнять geometry bounds без обводки либо распределить equal gaps по оси x/y. Reference page/selection/shape; shape требует reference_id. Возвращает job_id.",
  "corel_grid_duplicate": "Сетка по общему bounding box с обводкой; исходники образуют первую ячейку. Anchor — нижний левый угол первой ячейки в мм, до 1000 новых фигур. Возвращает job_id.",
  "corel_layout_shapes": "Детерминированная shelf-раскладка bounding boxes с обводкой, без поворотов/масштабирования/копий. Dry_run по умолчанию; apply требует revision и отсутствие unplaced. Не является фигурным nesting. Возвращает job_id.",
  "corel_inspect_curve": "Прочитать узлы/сегменты точной кривой и полный geometry_revision. Пагинация ограничивает выдачу, а не revision. Возвращает job_id.",
  "corel_create_curve": "Создать кривую из явных line/cubic сегментов и нескольких subpaths. Координаты в мм; до 10000 узлов. Возвращает job_id.",
  "corel_edit_curve": "Выполнить одну правку кривой при совпадении expected_revision. Индексы с единицы; join только концов в пределах tolerance_mm. Возвращает новый revision через job_id.",
  "corel_edit_text": "Буквально заменить, задать текст или формат всей истории точных текстовых фигур. Replace сохраняет форматирование за пределами совпадений; regex не поддерживается. Возвращает job_id.",
  "corel_find_text": "Буквальный поиск текста на точной странице без изменений. Фрагменты только include_text=true; regex запрещён. Возвращает job_id.",
  "corel_preflight": "Проверить контуры, размеры, обводку и наличие текста без исправлений. passed/failed/unknown по каждому правилу; не гарантирует пригодность станку. Возвращает job_id.",
  "corel_create_shapes": "Создать до 100 прямоугольников, эллипсов или линий на точном слое. Координаты в мм от нижнего левого угла страницы. Возвращает job_id.",
  "corel_transform_shapes": "Переместить, масштабировать, повернуть или отразить точные StaticID. Явный anchor в мм; scale только положительный. Возвращает job_id.",
  "corel_duplicate_shapes": "Дублировать точные StaticID с заданным шагом в мм, сохраняя исходники. До 1000 новых фигур с учётом групп. Возвращает job_id.",
  "corel_style_shapes": "Задать явную заливку и/или обводку точных StaticID. Цвет RGB/CMYK, ширина в мм; отсутствующие свойства сохраняются. Возвращает job_id.",
  "corel_manage_layers": "Создать, переименовать слой, изменить его флаги или перенести точные фигуры. Существующий слой задаётся opaque layer_id. Возвращает job_id.",
  "corel_group_shapes": "Сгруппировать фигуры одного слоя или разгруппировать ровно одну группу на один уровень. Возвращает job_id.",
  "corel_boolean_shapes": "Weld/trim/intersect двух фигур с явными keep_left/keep_right. Trim: LEFT вырезает RIGHT. Возвращает фактические ID через job_id.",
  "corel_create_text": "Создать artistic или paragraph текст с явным установленным шрифтом. BMP Unicode; неподдерживаемые символы отклоняются до мутации. Возвращает job_id.",
  "corel_convert_to_curves": "Преобразовать точные поддержанные фигуры или текст в кривые после проверки всех целей. Bitmap и эффекты не поддерживаются. Возвращает job_id.",
  "corel_status": "Подключиться к CorelDRAW 27: версия, состояние приложения и точные ID документов. Возвращает job_id.",
  "corel_inspect_document": "Ограниченная инспекция страницы и групп: StaticID, родитель, габариты, оформление, выделение. Текст только include_text=true. Возвращает job_id.",
  "corel_create_document": "Создать несохранённый документ в миллиметрах и вернуть document_id через задание.",
  "corel_open_document": "Открыть CDR и получить document_id. Возвращает job_id.",
  "corel_save_document": "Сохранить точный CDR; существующий файл назначения заменяется автоматически. Возвращает job_id.",
  "corel_close_document": "Закрыть точный документ; CorelDRAW не закрывается. Возвращает job_id.",
  "corel_export": "Экспорт PNG/PDF/SVG/JPEG/TIFF/AI/EPS/DXF/PLT через промежуточный файл с автоматической заменой назначения. Возвращает job_id.",
  "corel_preview": "PNG-превью страницы или выделения (`target=page|selection`). Получить изображение через corel_get_job.",
  "corel_describe_api": "Метаданные установленного API 27; выражения и вызовы запрещены. Возвращает job_id.",
  "corel_measure_shapes": "Измерить векторные фигуры или выделение: минимальная ширина и прямоугольник по объектам и набору. Возвращает job_id.",
  "corel_add_note": "Локально сохранить непроверенную находку. Текст не исполняется и не меняет скилл.",
  "corel_export_diagnostics": "Создать локальный краткий технический отчёт без кода, путей и пользовательского содержимого.",
  "corel_list_artifacts": "Показать размеры, возраст и защищённые артефакты заданий.",
  "corel_prune_artifacts": "Удалить выбранные внутренние артефакты после проверки prune_token; компактный отчёт и идемпотентность сохраняются.",
  "corel_create_batch": "Создать до 1000 фигур и надписей готовой пакетной операцией. Точные document/page/layer IDs, координаты в мм. Возвращает job_id.",
  "corel_update_batch": "Применить индивидуальный стиль и одну трансформацию к каждому из до 1000 точных StaticID. Сначала стиль, затем трансформация. Возвращает job_id."
}
```

Следующие записи перечисляют обязательные входы, значения enum и значения
по умолчанию. Они проверяются `npm run check:contracts` по собранным Zod-схемам
без запуска handlers или COM. Условные требования (например name для members)
по-прежнему проверяются схемой. `target` preview вычисляется как target, затем
legacy range, затем page; отдельного schema-default для target нет.

```jsonl
{"tool":"corel_create_batch","required":["request_id","document_id","page_index","layer_id","items"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_update_batch","required":["request_id","document_id","page_index","items"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_import","required":[],"defaults":{},"enums":{}}
{"tool":"corel_manage_pages","required":[],"defaults":{},"enums":{}}
{"tool":"corel_powerclip","required":[],"defaults":{},"enums":{}}
{"tool":"corel_align_shapes","required":[],"defaults":{},"enums":{}}
{"tool":"corel_grid_duplicate","required":["request_id","document_id","page_index","static_ids","rows","columns","anchor_x_mm","anchor_y_mm"],"defaults":{"deadline_seconds":600,"gap_x_mm":0,"gap_y_mm":0},"enums":{}}
{"tool":"corel_layout_shapes","required":[],"defaults":{},"enums":{}}
{"tool":"corel_inspect_curve","required":["document_id","page_index","static_id"],"defaults":{"deadline_seconds":600,"offset":0,"limit":100},"enums":{}}
{"tool":"corel_create_curve","required":["request_id","document_id","page_index","layer_id","subpaths"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_edit_curve","required":[],"defaults":{},"enums":{}}
{"tool":"corel_edit_text","required":[],"defaults":{},"enums":{}}
{"tool":"corel_find_text","required":["document_id","page_index","query","case_sensitive"],"defaults":{"deadline_seconds":600,"offset":0,"limit":100,"include_text":false,"snippet_limit":200,"max_visited":10000},"enums":{}}
{"tool":"corel_preflight","required":["document_id","page_index","static_ids","checks"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_create_shapes","required":["request_id","document_id","page_index","layer_id","items"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_transform_shapes","required":[],"defaults":{},"enums":{}}
{"tool":"corel_duplicate_shapes","required":["request_id","document_id","page_index","static_ids","copies","step_x_mm","step_y_mm"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_style_shapes","required":["request_id","document_id","page_index","static_ids"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_manage_layers","required":[],"defaults":{},"enums":{}}
{"tool":"corel_group_shapes","required":[],"defaults":{},"enums":{}}
{"tool":"corel_boolean_shapes","required":["request_id","document_id","page_index","action","left_id","right_id","keep_left","keep_right"],"defaults":{"deadline_seconds":600},"enums":{"action":["weld","trim","intersect"]}}
{"tool":"corel_create_text","required":[],"defaults":{},"enums":{}}
{"tool":"corel_convert_to_curves","required":["request_id","document_id","page_index","static_ids"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_status","required":[],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_inspect_document","required":["document_id"],"defaults":{"offset":0,"limit":100,"recursive":false,"max_depth":8,"max_visited":10000,"include_text":false,"text_limit":1000,"deadline_seconds":600},"enums":{}}
{"tool":"corel_create_document","required":["request_id"],"defaults":{"width_mm":210,"height_mm":297,"deadline_seconds":600},"enums":{}}
{"tool":"corel_open_document","required":["request_id","path"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_save_document","required":["request_id","document_id"],"defaults":{"deadline_seconds":600},"enums":{}}
{"tool":"corel_close_document","required":["request_id","document_id"],"defaults":{"discard_changes":false,"deadline_seconds":600},"enums":{}}
{"tool":"corel_export","required":["request_id","document_id","path","format"],"defaults":{"range":"page","dpi":150,"deadline_seconds":600},"enums":{"format":["png","pdf","svg","jpg","jpeg","tiff","tif","ai","eps","dxf","plt"],"range":["page","selection","document"],"color_mode":["rgb","cmyk"],"tiff_compression":["lzw","none"],"text_mode":["preserve","curves"]}}
{"tool":"corel_preview","required":["document_id"],"defaults":{"max_size":1200,"deadline_seconds":600},"enums":{"target":["page","selection"],"range":["page","selection"]}}
{"tool":"corel_describe_api","required":["kind"],"defaults":{"filter":"","offset":0,"limit":100,"deadline_seconds":600},"enums":{"kind":["types","members","enum"]}}
{"tool":"corel_measure_shapes","required":["document_id"],"defaults":{"tolerance_mm":0.01,"deadline_seconds":600},"enums":{}}
{"tool":"corel_add_note","required":["request_id","category","text"],"defaults":{},"enums":{"category":["lesson","limitation","idea"]}}
{"tool":"corel_export_diagnostics","required":["request_id","job_id"],"defaults":{"overwrite":false},"enums":{}}
{"tool":"corel_list_artifacts","required":[],"defaults":{"offset":0,"limit":50},"enums":{}}
{"tool":"corel_prune_artifacts","required":["request_id","candidates"],"defaults":{},"enums":{}}
{"tool":"corel_get_job","required":["job_id"],"defaults":{"wait_seconds":0,"log_lines":50,"include_preview":false},"enums":{}}
{"tool":"corel_guide","required":[],"defaults":{"offset":0,"limit":8000},"enums":{"topic":["skill","tools","operations","api","recipes","pitfalls","install"]}}
```
