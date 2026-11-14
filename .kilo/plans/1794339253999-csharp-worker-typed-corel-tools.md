# План: C#-исполнитель и типизированные инструменты CorelDRAW 27

**Цель:** заменить PowerShell COM-исполнитель и все исполняемые VBA-зависимости C#-исполнителем; заменить `corel_execute_vba` и `corel_build_vba` готовыми операциями, включая согласованный расширенный набор.

**Архитектура:** TypeScript MCP-сервер сохраняет задания, request_id, отчёты и вычислительную геометрию. Отдельный C# EXE принимает JSON Lines через stdin/stdout и выполняет COM только на одном STA-потоке с циклом сообщений и межпроцессным mutex. Python host и React-панель используют новый каталог, не исполняют код модели.

**Стек:** Node.js 24, существующие TypeScript/MCP/Zod, C# `net10.0-windows`, Windows x64, встроенный COM Interop и VGCore 27, System.Text.Json, xUnit для нового исполнителя. Публикация self-contained, без trimming, Native AOT и single-file на первом этапе.

**Спецификация:** согласованные ниже решения находятся в этом же документе. При реализации использовать навыки `executing-plans` или `subagent-driven-development`, затем `test-driven-development` и `verification-before-completion`. Коммиты и установка в пользовательское окружение только по отдельному разрешению.

## 1. Согласованные границы

- Только готовые операции. Нет `corel_execute_csharp`, Roslyn scripting, произвольных выражений, универсального вызова COM-методов по имени или скрытого VBA fallback.
- Сохранить текущие документные инструменты, API-метаданные, экспорт, превью, измерение, локальные задания и артефакты.
- Перенести сценарии существующих VBA-рецептов: геометрия, текст, преобразования, копирование, стили, слои/группы, булевы операции, чтение кривых.
- Добавить подготовку к производству, прямоугольную раскладку, импорт/сборку, редактирование кривых, поиск/замену и базовое форматирование текста.
- Не входят: фигурный nesting, OCR, трассировка bitmap, автоматическое исправление всего макета, гарантия совместимости со станком, восстановление сложной топологии, редактирование live effects, автоматическая подмена шрифтов, полноценное управление ICC-профилями.
- PowerClip поддерживается отдельными операциями; расширение геометрического измерителя на PowerClip не входит.
- PowerShell-скрипты установки/жизненного цикла панели допустимы. Цель не состоит в переписывании всей системы на C#.

## 2. Проверенное состояние репозитория

- `src/executor.ts:15-86`: постоянный PowerShell-процесс, JSON Lines, один pending job. `src/jobs.ts:141-227`: второй job получает BUSY; deadline не освобождает слот.
- `powershell/CorelAutomation.psm1:634-705`: mutex `Local\CorelDrawMcp_v27`, привязка документа, прямые COM-операции и dispatch VBA.
- `powershell/CorelAutomation.psm1:78-83`: почти все операции требуют VBE idle. Эту обязательную зависимость убрать, не выдавая отсутствие VBE за доказательство отсутствия сторонних операций.
- `powershell/GeometryExtraction.psm1:109-112` и `powershell/M_GeometrySnapshot.bas`: скрытое исполнение VBA внутри `corel_measure_shapes`.
- `powershell/ApiMetadata.cs`: уже существующая C#-реализация чтения type library; переносить, а не писать заново.
- `src/tools.ts:206-221`: публичные build/execute VBA. `test/integration*.mjs` создают тестовые объекты через VBA, поэтому переносить и setup тестов.
- `webhost/corel_agent/policy.py`: отдельный ограниченный каталог агента и таблица postchecks; новых MCP-инструментов недостаточно без обновления host.
- `webhost/corel_agent/cleanup.py`: исторические retained GMS участвуют в safety barrier; нельзя просто вырезать проверку и снять блокировки.
- В рабочем дереве уже есть чужие изменения `webui/` и отчёты `.superpowers/`. Не откатывать и не перезаписывать их; перед реализацией сверить актуальное состояние.
- Текущий код разрешает save/export без отдельного overwrite-подтверждения. Этот план не меняет продуктовую политику разрешений; ориентироваться на актуальные исходники, не на старые записи памяти.

## 3. Официальная документация и степень подтверждения

Изучены через Context7 официальные страницы SDK CorelDRAW 27 и напрямую Microsoft Learn:

| Источник | Что обосновывает |
| --- | --- |
| [CorelDRAW 27: CreateDocument, C#](https://community.coreldraw.com/sdk/api/draw/27/m/application.createdocument?lang=cs) | Объектная модель имеет C#-сигнатуры; прямой вызов из C# вместо VBA |
| [CorelDRAW 27: EndCommandGroup](https://community.coreldraw.com/sdk/api/draw/27/m/document.endcommandgroup?lang=cs) | Обязательное закрытие успешно открытой группы команд; иначе риск повреждения undo-стека |
| [CorelDRAW 27: Layer.ImportEx](https://community.coreldraw.com/sdk/api/draw/27/m/layer.importex?lang=cs) и [ImportFilter.Finish](https://community.coreldraw.com/sdk/api/draw/27/m/importfilter.finish) | Двухэтапный импорт через фильтр, отдельная проверка неинтерактивного выполнения |
| [Microsoft: COM Interop](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/cominterop) | Встроенный COM Interop доступен в современном .NET на Windows |
| [Microsoft: STA](https://learn.microsoft.com/en-us/windows/win32/com/single-threaded-apartments) | Цикл сообщений, COM-маршалинг и опасность повторного входа |
| [Microsoft: managed threads и COM](https://learn.microsoft.com/en-us/dotnet/standard/threading/managed-and-unmanaged-threading-in-windows) | ApartmentState до запуска потока; ThreadPool использует MTA |
| [Microsoft: interop assembly из type library](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/how-to-generate-interop-assemblies-from-type-libraries) | Генерация типизированной сборки через Tlbimp |
| [Microsoft: публикация .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/) | Self-contained win-x64; runtime обновляется вместе с пакетом |
| [Microsoft: примитивы синхронизации](https://learn.microsoft.com/en-us/dotnet/standard/threading/overview-of-synchronization-primitives) | Именованный mutex межпроцессный; освобождается потоком-владельцем |

Документация не является доказательством runtime-совместимости конкретной установки. В ответах Context7 встречаются неполные пересказы сигнатур, в частности CreateSubPath; не переносить их в код без проверки type library 27. Прямое извлечение части страниц Corel в этой сессии вернуло пустое содержимое. Страницы старых версий из поисковой выдачи не считать подтверждением API 27.

Для каждой операции при реализации фиксировать точный URL SDK 27, C#-сигнатуру из установленной type library, enum-значения и live-тест. Проверять классы Application, Document, Page, Layer, Shape/ShapeRange, Curve/SubPath/Node/Segment, Text/TextRange, PowerClip, ImportFilter. Отсутствующая сигнатура или несовместимость обязательной операции блокирует соответствующий этап, а не разрешает fallback.

## 4. Решения по исполнителю

1. Новый каталог `worker/`, EXE `CorelMcp.Worker.exe`. Сохраняются `src/executor.ts` и интерфейс Executor, класс становится `CSharpExecutor`; точка создания обновляется в `src/index.ts`.
2. Типизированная interop assembly создаётся на build-машине из официально установленной VGCore 27. Скрипт ищет зарегистрированную type library, проверяет GUID/version/архитектуру, пишет fingerprint входа. Нет скачивания случайной Interop DLL. До упаковки проверить условия распространения Corel interop; при отсутствии разрешённого способа упаковка блокируется.
3. Использовать встроенный RCW interop. Не вводить ComWrappers/source generators без доказанной необходимости. Закрепить точную установленную .NET 10 SDK-версию в `global.json` при первом этапе; не обновлять её молча.
4. STA host с Windows Forms `Application.Run(ApplicationContext)` без видимой формы; работа приходит через dispatcher. Отдельное чтение stdin не обращается к COM. Синхронные обработчики операций не переходят на ThreadPool. COM-объекты не выходят за границу STA; наружу только DTO.
5. При обработке сообщений запретить повторный dispatch до завершения текущей операции, включая cleanup. Очередь не изменяет текущую семантику: один принятый job, конкурентный запрос получает BUSY, а не скрыто накапливается.
6. Mutex с прежним именем на время всей COM-операции. WaitOne(0), ReleaseMutex на том же STA. Abandoned mutex означает неопределённое состояние, не разрешение повторить мутацию; вернуть диагностическую ошибку без изменения документа и потребовать сверку состояния.
7. Сохранить активацию `CorelDRAW.Application.27` и проверку версии. При переподключении новая session identity, старые document_id недействительны. Никакого переключения на случайный ActiveDocument при ошибке ID.
8. RCW lifetime управляется явно на STA с учётом alias/shared references; не вызывать FinalReleaseComObject для всех полученных ссылок и не использовать принудительный GC как нормальный cleanup. Документные ссылки удерживаются до закрытия/инвалидации сессии.
9. Нет автоматического повторения COM-мутаций, reconnect-and-replay, Thread.Abort, Application.Quit, закрытия чужих документов или автоматического нажатия модальных диалогов.
10. Mutex защищает только сотрудничающие процессы MCP, не UI пользователя и сторонние макросы. Все предусловия проверять непосредственно перед действием; несогласованность после вызова отражать как ошибку/неопределённость, а не как rollback.

## 5. Протокол и общие контракты

- Сохранить поля request `{job_id, tool, args, run_directory}`, progress `{type:'progress',job_id,stage}` и result `{type:'result',job_id,data:WorkerResult}`. Удалить только активные VBA-handshake `validate_json`/`observer_prepare`/`observer_finish`.
- Добавить ready handshake `{type:'ready', protocol_version:1, worker_version, runtime, architecture}` до dispatch. Не смешивать его со stdout MCP. На worker stdout только протокол, diagnostics в stderr; сообщения пользователю на русском.
- Ограничить одну JSONL-запись 4 МиБ в обоих направлениях; duplicate JSON keys, неизвестные поля и нечисловые/неfinite значения отклонять. Крупные данные возвращать как артефакты с имеющейся проверкой путей, а не наращивать строку без ограничения.
- Сохранить request_id/fingerprint, неизменяемость завершённых отчётов, `unknown`, `timed_out` и late result. Deadline не прерывает COM и не освобождает слот. Exit после dispatch до результата означает unknown. Невозможность запуска до dispatch классифицируется как доказанное невыполнение.
- Polling/list jobs читает только сохранённые данные, никогда COM. При ошибке протокола незавершённая операция не считается завершённой.
- Для новых мутаций `request_id`, `document_id`, `page_index` обязательны; идентификаторы фигур только StaticID. Число целей 1..100 без дублей. Никакого неявного fallback на выделение. Старые инструменты сохраняют свои прежние defaults.
- Единицы новых инструментов: мм, углы в градусах; origin нижний левый угол заданной страницы, X вправо/Y вверх. Адаптер переводит координаты с учётом документных единиц и origin; не полагаться на активную страницу или ReferencePoint.
- Создание до 100 объектов за вызов; тиражирование до 1000 новых фигур; размеры (0,100000] мм, координаты [-1000000,1000000] мм, тексты до 100000 UTF-16 code units, суммарные узлы редактируемого контура до 10000. Действующие лимиты существующих инструментов не уменьшать.
- Результат мутации содержит `affected_static_ids`, `created_static_ids`, `deleted_static_ids`, фактические параметры результата; для ошибок `applied:false` только при доказанном отсутствии изменений. Частичное действие не обозначать как полный успех и не обещать откат.
- Cleanup восстанавливает временно изменённые Unit/ReferencePoint/Optimization/EventsEnabled/PreserveSelection, активный документ/страницу и выделение живых объектов. Не восстанавливать значения, являющиеся явной целью операции. Удалённые ID нельзя восстановить в selection; возвращать это явно.
- Группа undo открывается только после preflight и закрывается в finally, если BeginCommandGroup успешно вернулся. Это не транзакция; crash процесса может помешать finally. Ошибки действия и cleanup сохранять отдельно.
- Page/layer получают session-scoped opaque IDs в инспекции; новые команды для слоёв используют layer_id, а не неуникальное имя. page_index проверяется при dispatch; для операций, меняющих страницы, использовать page_id. Закрытие/переподключение инвалидирует все такие ID.

## 6. Каталог новых операций

Все схемы строгие. Дискриминатор action/kind имеет закрытый enum, не имя COM-метода. В каждом модуле сначала закрепить схему, результат и негативные тесты, затем реализовать адаптер API.

| Инструмент | Контракт и границы |
| --- | --- |
| `corel_create_shapes` | `items[]`: rectangle(x,y,width,height), ellipse(x,y,width,height), line(x1,y1,x2,y2); layer_id обязателен, результат по каждому item |
| `corel_transform_shapes` | `static_ids`, action move(dx,dy), scale(sx,sy,anchor_x,anchor_y), rotate(angle,anchor_x,anchor_y), flip(axis,anchor); scale > 0, отражение только flip |
| `corel_duplicate_shapes` | `static_ids`, copies 1..1000, step_x_mm/step_y_mm; общий лимит новых фигур 1000, исходники сохраняются |
| `corel_style_shapes` | Явные fill none/uniform и outline none/uniform с шириной; RGB целые 0..255, CMYK 0..100; неуказанные свойства не менять |
| `corel_manage_layers` | action create(name,page_id), rename(layer_id,name), set_flags(layer_id,visible/editable/printable), move_shapes(layer_id,static_ids); удаление слоёв вне первой версии |
| `corel_group_shapes` | action group/ungroup, точные ID; group только на одной странице/слое, ungroup ровно одной группы, без рекурсивного уничтожения вложенности |
| `corel_boolean_shapes` | action weld/trim/intersect; left_id/right_id различны, явные keep_left/keep_right; trim означает LEFT вырезает RIGHT; возвратить заменённые и новые ID |
| `corel_create_text` | artistic/paragraph, Unicode text, позиция или рамка, font_family/font_size_pt; шрифт отсутствует -> ошибка до мутации, не подстановка |
| `corel_edit_text` | static_ids текстовых фигур, action replace_literal(old,new,case_sensitive)/set_text/format; формат всей истории выбранного text shape: family,size,bold,italic,paragraph alignment/line spacing; частичные диапазоны вне первой версии |
| `corel_find_text` | Read-only: буквальный поиск, case_sensitive, page_index, пагинация; ID, число совпадений и bounded фрагмент только include_text=true; regex не принимается |
| `corel_inspect_curve` | Read-only: StaticID, подконтура, узлы/сегменты, позиции и control points, geometry_revision; pagination offset/limit 1..500, весь revision считается по полной bounded геометрии |
| `corel_create_curve` | line/cubic сегменты, старт каждого subpath, closed; явные control points, лимит 10000 узлов; без выражений и исполнения |
| `corel_edit_curve` | Одна фигура + expected_revision; одна action move_node/insert_node/delete_node/set_node_type/close_subpath/join_endpoints; индексы 1-based из inspect; вставка через segment_index и t в (0,1), join только концов внутри этой фигуры; новый revision в результате |
| `corel_convert_to_curves` | Точные ID поддержанных фигур/текста; preflight всех целей, карта исходных и итоговых ID; эффекты/bitmap явно неподдерживаемы |
| `corel_align_shapes` | axis x/y, align min/center/max либо distribute equal_gaps; reference page/selection/shape с explicit reference_id; bounding boxes, не визуальный центр масс |
| `corel_grid_duplicate` | rows/columns 1..1000, gap_x/gap_y >=0, anchor, static_ids; объединённый bounding box исходного набора как ячейка; исходники составляют первую ячейку, общий лимит 1000 копий |
| `corel_layout_shapes` | Детерминированная shelf-раскладка axis-aligned bounding boxes: порядок входных ID, слева направо, снизу вверх, перенос строки; margins/gaps >=0, без поворотов, масштабирования и копий; dry_run=true по умолчанию |
| `corel_preflight` | Read-only: ограниченный список проверок open_contours, dimensions, outline_width, outline_color, text_present; параметры допусков обязательны для численных/цветовых правил; passed/failed/unknown и причины по каждой цели |
| `corel_import` | Один локальный SVG/PDF/PNG/JPEG <=256 МиБ, SHA-256, слой, placement x/y и необязательная рамка с сохранением пропорций; PDF только явно выбранная одна страница; без сетевых загрузок и диалогов |
| `corel_manage_pages` | action add(count,size), resize(page_id,width,height); до 100 страниц за вызов; удаление/перестановка вне первой версии |
| `corel_powerclip` | action place(container_id,static_ids,center=false)/extract(container_id); исключить self/ancestor cycles; извлечение содержимого без удаления контейнера; вернуть перемещения и новые ID |

Layout требует request_id в обоих режимах; инструмент консервативно имеет readOnlyHint=false. dry_run возвращает placements/unplaced и layout_revision по входной геометрии/странице/параметрам. Применение требует этот revision и новый request_id; изменение состояния -> STALE_LAYOUT. Если есть unplaced, apply отвергается до первой мутации; частичное размещение только отдельным явно уменьшенным набором. Не объявлять такой алгоритм оптимальным nesting.

geometry_revision включает полный порядок subpaths/nodes, координаты/control points/типы/closed и transform, без округления и locale-зависимых строк. Несовпадение -> STALE_GEOMETRY до изменения; даже при совпадении нельзя обещать атомарность относительно ручного редактирования Corel.

Импорт: сначала проверенная частная копия файла в run_directory, сверка digest, затем импорт в принадлежащий job временный документ; проверка результата и перенос в целевой слой. Если SDK-фильтр требует диалог/не позволяет выбранную PDF-страницу, вернуть FILTER_UNSUPPORTED, не выбирать страницу 1 молча. Доказать обнаружение импортированных фигур без зависимости от случайного выделения. Успех не означает визуальную идентичность исходного PDF; шрифты/эффекты проверяются и ограничения отражаются в отчёте.

## 7. План работ и проверки

### Этап 1. Подтвердить C# / VGCore и сборку

Файлы: создать `worker/CorelMcp.Worker/CorelMcp.Worker.csproj`, `Program.cs`, `Interop/CorelConnection.cs`, `worker/CorelMcp.Worker.Tests/`, `scripts/build-worker.ps1`, `global.json`.

- [ ] Сначала тесты подключения с неподходящей версией/отсутствующей регистрацией и тест thread affinity; затем минимальный EXE и получение interop assembly.
- [ ] Реализовать `CorelConnection.Connect(string progId)` на STA с проверкой версии 27; сохранить семантику текущей COM-активации, не добавлять запуск через shell.
- [ ] На двух временных документах проверить CreateDocument, создание прямоугольника, чтение StaticID, текст Unicode, Begin/EndCommandGroup, preview/export, cleanup, исходный активный документ.
- [ ] Проверить обычный запуск без доступа к VBA project model; отдельно подтвердить отсутствие обращений к InitializeVBA/VBE/RunMacro.
- [ ] Зафиксировать проверенные SDK-сигнатуры и fingerprint type library в `worker/api-contract.json`, live-результаты как артефакты теста. Неподтверждённая совместимость блокирует дальнейший перенос; никаких молчаливых переходов на .NET Framework/VBA.

Приёмка: `dotnet test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj`; opt-in live smoke на временных документах с сохранением диагностики. .NET 10 и права распространения interop подтверждены до поставки.

### Этап 2. Надёжный протокол и STA host

Файлы: создать `Protocol.cs`, `StaHost.cs`, `OperationDispatcher.cs` в worker; изменить `src/executor.ts`, `src/index.ts`, `src/types.ts`; тесты `test/executor.test.mjs`, `test/jobs.test.mjs`, новые `ProtocolTests.cs`, `StaHostTests.cs`.

- [ ] Сначала тесты ready/version mismatch, fragmented UTF-8, CRLF, oversize, duplicate keys, ошибочного job_id, stdout noise, EOF, exit до/после dispatch.
- [ ] Реализовать интерфейс `WorkerResult Execute(WorkerRequest request, Action<string> progress)` и immutable DTO из раздела 5; CSharpExecutor сохраняет Executor.run/close.
- [ ] Проверить один STA managed thread ID во всех COM вызовах и cleanup, отсутствие повторного входа при message pumping и конкурентных запросах.
- [ ] Проверить два процесса под прежним mutex, BUSY и abandoned mutex. Проверить deadline, поздний результат, смерть worker и сохранение неизвестного исхода без retry.
- [ ] Сохранить расчёт measureSnapshot и проверку snapshot_path на стороне TS. Не переписывать jobs целиком ради смены backend.

Приёмка: `npm test` и `dotnet test ...`; COM-free tests исполняются без установленного Corel. До финального cutover C# выбирается только явным development-параметром, выбор backend фиксируется на процесс, fallback запрещён.

### Этап 3. Перенести существующие COM-функции и геометрию

Файлы worker: `DocumentRegistry.cs`, `StateScope.cs`, `Operations/Documents.cs`, `Inspection.cs`, `Export.cs`, `ApiMetadata.cs`, `GeometrySnapshot.cs`. Источники переноса: `powershell/CorelAutomation.psm1`, `ApiMetadata.cs`, `GeometryExtraction.psm1`, `M_GeometrySnapshot.bas`.

- [ ] Зафиксировать compatibility fixtures полей/ошибок старых status/inspect/create/open/save/close/export/preview/describe/measure. Убрать VBA-поля из новых status, добавить worker/runtime/protocol capabilities; исторические отчёты по-прежнему читаются.
- [ ] Перенести документные ID и проверки страницы/слоя; никогда не считать совпадение имени документа его идентичностью.
- [ ] Перенести форматы/параметры экспорта и временную публикацию файлов без изменения нынешнего overwrite-контракта.
- [ ] Перенести снимок геометрии: изолированная копия, проверка StaticID и физической геометрии, частная копия каждой цели, materialize outline, convert text в копии, line/cubic сегменты; 64 МиБ, 200000 сегментов, depth 32.
- [ ] Проверить точность прежних измерений, mm/inch, RU/EN locale, группы вместе с дочерними целями, no-fill, Unicode; unsupported geometry остаётся явной ошибкой.
- [ ] Проверить восстановление active doc/page/selection, source Dirty, cleanup при ошибках и при заполненном диске. Ни один тест setup не должен нуждаться в VBA в новом backend.

Приёмка: прежние тесты поведения заменяют PowerShell source-string assertions там, где реализация удаляется; `test/geometry.test.mjs` проходит без изменения алгоритма. Live differential сравнение на отдельных одинаковых временных документах, не повторение мутации в одном документе.

### Этап 4. Основные типизированные операции

Файлы: `src/drawing-tools.ts` с подключением из `src/tools.ts`; worker `Operations/Shapes.cs`, `Styles.cs`, `Layers.cs`, `Text.cs`; `test/drawing-tools.test.mjs`, `test/integration-drawing.mjs`, соответствующие C# Tests.

- [ ] Сначала строгие schemas и negative fixtures для create/transform/duplicate/style/layers/group/boolean/create_text/convert_to_curves из раздела 6.
- [ ] Реализовать preflight всех целей: документ, страница, StaticID, поддержанный тип, locked layer, предельное число результатов; отказ до открытия undo group при неверном вводе.
- [ ] Перенести все существующие рецепты как тестовые сценарии вызовов инструментов, а не как C# сниппеты, поступающие от модели.
- [ ] Проверить translate/scale/rotate относительно заданного anchor, trim direction и keep flags, выходные ID после replacement, неуникальные layer names, сохранение selection.
- [ ] Fault injection после первой фактической мутации доказывает partial failure/applied и отсутствие автоматического undo/replay. Test-only fault adapter не входит в production EXE.

Приёмка: unit tests, protocol tests, opt-in live сценарии базовых рецептов и неизменность контрольного документа.

### Этап 5. Кривые, текст и производственные проверки

Файлы: worker `Operations/Curves.cs`, `Text.cs`, `Preflight.cs`; `src/drawing-tools.ts`; новые `CurveTests.cs`, `TextTests.cs`, `PreflightTests.cs`, `test/integration-production.mjs`.

- [ ] Сначала контракты inspect/create/edit curve и revision; fixtures line/cubic, несколько subpaths, closed/open, stale revision, отрицательные индексы, удаление последнего допустимого узла, недопустимый join.
- [ ] Реализовать одну явную curve-edit action за вызов; insertion сохраняет геометрию сегмента, join проверяет расстояние между концами относительно явного tolerance_mm, не соединяет произвольные узлы.
- [ ] Реализовать буквальный find/replace, set_text и whole-story formatting; протестировать кириллицу, emoji/суррогатные пары, переносы строк, mixed formatting и отсутствующий шрифт. replace не должен пересобирать весь текст с потерей чужого форматирования.
- [ ] Preflight не мутирует. Для неподдержанных объектов/лимитов вернуть unknown, а не passed. Проверки стиля резки применяют только заданные численные/цветовые критерии и не утверждают пригодность станку.
- [ ] Рецепт подготовки: preflight -> явные convert/style -> повторный preflight; исправления сами не запускаются.

Приёмка: unit tests и live fixture с ожидаемыми координатами/числом узлов/текстом/замечаниями, сохранение исходников при read-only вызовах.

### Этап 6. Раскладка и тиражирование

Файлы: worker `Operations/Layout.cs`, `LayoutPlanner.cs`; `src/drawing-tools.ts`; `LayoutTests.cs`, `test/integration-layout.mjs`.

- [ ] Сначала pure tests выравнивания, equal gaps, grid и shelf planner: пустой набор, слишком крупный объект, margins, gaps, граница страницы, zero-size shape, лимит 1000 копий.
- [ ] Зафиксировать geometry bounding boxes без обводки для align; для layout/grid включать outline bounds, если их API может измерить, иначе явная unsupported ошибка. Не обещать минимальный межконтурный зазор по bounding boxes.
- [ ] Реализовать dry_run/revision/apply, отказ при unplaced, invalidation после редактирования фигуры/страницы. Детерминированный входной порядок, без случайной оптимизации.
- [ ] Live проверить фактические bounds и число копий; сбой после создания части копий возвращает созданные ID и partial outcome.

Приёмка: planner tests без Corel, затем временный лист с проверкой gaps/margins и отсутствия лишних изменений.

### Этап 7. Импорт, страницы, PowerClip

Файлы: worker `Operations/Import.cs`, `Pages.cs`, `PowerClip.cs`; `src/drawing-tools.ts`; `ImportTests.cs`, `PowerClipTests.cs`, `test/integration-import.mjs`.

- [ ] Сначала fixtures file identity/size/extension, locked layer, page_id, контейнер сам в себе/предок, повреждённый PDF/SVG, недоступная PDF-страница, запрос диалога фильтром.
- [ ] Сверить точные свойства фильтров SDK 27 и установленной type library. Реализовать импорт и перенос только принадлежащих job фигур; не использовать clipboard пользователя.
- [ ] Внешние SVG resource references запретить до передачи фильтру; сетевую недоступность native фильтров не выдавать за sandbox. PDF password/dialog -> явная ошибка, не автоматическое взаимодействие с UI.
- [ ] Создание/resize страниц не трогает несвязанные страницы. PowerClip placement не меняет размер содержимого без явного параметра; проверить координаты и структуру после extract.
- [ ] Сохранить артефакты ошибочного импорта и cleanup; если подтвердить удаление временного документа не удалось, не помечать cleanup.completed=true.

Приёмка: live SVG/PDF/PNG/JPEG fixtures; данные пользователя и чужие документы неизменны; нет GMS/VBE и диалогового fallback.

### Этап 8. Подключить каталог к агенту и истории

Файлы: `webhost/corel_agent/policy.py`, `runtime.py`, `contracts.py`, `persisted.py`, `recovery.py`, `cleanup.py`, `privacy.py`, `observability.py`; `webhost/tests/`; минимальные изменения `webui/src/components/MessageList.tsx`, `RunPanel.tsx`, `RecoveryPanel.tsx` и соответствующих tests.

- [ ] Сначала tests нового каталога, JSON Schema во всём пути model -> host -> MCP -> worker, frozen document binding и owner identity перед dispatch.
- [ ] Расширить capability table; read-only tools: inspect_curve/find_text/preflight и существующие status/inspect/preview/describe/measure. Layout консервативно остаётся mutating-инструментом в policy в обоих режимах и всегда получает request_id; dry_run не делает изменений, но не требует новой условной системы разрешений.
- [ ] После drawing mutation резервировать бюджет на inspect и preview, document operations сохраняют действующие postchecks. Curve/layout apply revision нельзя генерировать host из воздуха; он поступает из предыдущего результата инструмента.
- [ ] Не увеличивать автоматически предел ходов/вызовов агента. Проверить реалистичный многокомандный сценарий в текущем бюджете; нехватка бюджета завершает Run до следующей мутации.
- [ ] Удалить VBA-инструкции из prompts, текущих меню и новых prepared operations. Сохранить чтение старых сообщений/операций с прежними tool names; они никогда не переисполняются при продолжении беседы.
- [ ] Сохранить CSRF/bootstrap, Credential Manager, secret redaction, network contract provider и текущую политику доступа. Не добавлять новые UI-переключатели разрешений в этой миграции.
- [ ] Для import host связывает frozen input fingerprint и worker проверяет частную копию; paths/secrets не попадают в technical.jsonl. UI показывает параметры/результат операции вместо окна исходного VBA.

Приёмка: host non-live tests, OpenAPI/codegen checks, browser tests; существующий Codex-like дизайн не переделывается.

### Этап 9. Cutover, удаление VBA и упаковка

Файлы: `src/server.ts`, `tools.ts`, `types.ts`, `jobs.ts`, `artifacts.ts`, `vba-observer.ts`, `guides.ts`, `version.ts`, `package.json`; `powershell/*`; `scripts/Install-CorelMcp.ps1`, `Install-CorelAgent.ps1`, `install-codex.mjs`, `Verify-CorelAgent.ps1`; `skills/coreldraw-automation/`, `README.md`, `AGENTS.md`.

- [ ] Перед обновлением блокировать активные/running/timed_out/unknown задания и неразрешённые retained GMS текущего окружения. Их разрешение выполняется старой установленной версией/оператором, без автоматического replay, удаления файлов или изменения outcome.
- [ ] Добавить read-only pre-upgrade проверку статуса текущего owner и каталогов отчётов/host history. Если проверка неполная, обновление не активируется. Старые отчёты остаются на месте.
- [ ] После прохождения всех обязательных live gates сделать C# единственным backend. Удалить execute/build VBA из регистрации, worker dispatch, prompts и текущих examples; обращение к старому tool отклоняется, не перенаправляется на похожую операцию.
- [ ] Удалить PowerShell COM worker, VBA template/harness, observer process и активные observer handshakes. Перенесённый ApiMetadata.cs удалить из старого каталога. Исторические DTO/parser для отчётов и защиту prune сохранить там, где это нужно persisted данным.
- [ ] Заменить .bas examples готовыми JSON-сценариями вызовов; обновить guides, контрактные проверки, описание пакета, prerequisite VBA/PowerShell-worker и AGENTS.md. Пользовательские GMS не трогать.
- [ ] Публиковать `worker/publish/win-x64/CorelMcp.Worker.exe` и зависимости через `dotnet publish worker/CorelMcp.Worker/CorelMcp.Worker.csproj -c Release -r win-x64 --self-contained true -o worker/publish/win-x64`; publish directory исключить из git.
- [ ] Инсталлятор проверяет комплектность/version handshake; исходная сборка требует SDK и type library, готовый пакет не требует .NET SDK или VBA. Удалять/обновлять только собственные файлы пакета.
- [ ] Откат только целого релиза после остановки owner и проверки отсутствия незавершённого COM. Никакого автоматического rollback backend после ошибки задания.

Приёмка: установка готового пакета в чистый тестовый каталог, без глобального SDK/runtime; подключение к Corel 27 без разрешения VBE; исторические failed/unknown отчёты читаются, barrier не снимается, автоматический prune их не удаляет.

### Этап 10. Итоговая проверка

- [ ] `dotnet test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj -c Release`.
- [ ] `npm test` и `npm run check:contracts`.
- [ ] `powershell -NoProfile -File scripts/Verify-CorelAgent.ps1`, расширенный .NET-тестами и проверкой packaged worker.
- [ ] `npm run test:integration`, обновлённый всеми integration-drawing/production/layout/import сценариями, только в разрешённом live-окружении.
- [ ] Отдельный opt-in panel live smoke с двумя temporary documents через `CORELDRAW_AGENT_LIVE_FIXTURE`; не считать его автоматически покрытым обычным Verify gate.
- [ ] Сравнить cold start, создание/чтение 1000 фигур, экспорт и прежние измерения. Числа включить в отчёт, не заявлять ускорение C# без измерений.
- [ ] Статическая проверка shipped runtime: нет RunMacro/InitializeVBA/VBE, динамической компиляции, shell/Invoke-Expression и исполняемых .bas/.gms. Legacy-тексты в исторических readers/тестовых fixtures допустимы и отдельно перечислены.

## 8. Главные риски и критерии завершения

- STA pumping допускает reentrancy: тестировать dispatch guard, а не считать однопоточность достаточной защитой.
- Точный SDK build, redistributability interop и поведение импортных фильтров являются проверяемыми предпосылками этапов, не установленными фактами этой сессии. Если они не подтверждаются, зафиксировать blocker и пересогласовать техническое решение; не уменьшать согласованный каталог молча.
- При crash/зависшем COM finally может не выполниться. Отчёт unknown и ручная сверка обязательны; CorelDRAW не убивается для восстановления mutex.
- Внешние COM-вызовы в цикле могут быть медленнее VBA внутри Corel. Использовать ShapeRange и пакетные API там, где они сохраняют контракт; не заменять измерения рекламным обещанием скорости.
- Состояние документов меняется вручную. Проверять ID/revision перед действием, возвращать только наблюдаемые результаты, не обещать блокировку UI.
- Native import filters и шрифты зависят от установки. Неподдержанный фильтр/неизвестная полнота проверки не считается успехом.
- Историческая совместимость нужна для сохранённых отчётов, но не для исполнения старого кода. Обновление не должно незаметно обходить recovery barrier.

Работа завершена только когда весь согласованный каталог имеет строгие схемы, документированные API 27 и успешные обязательные проверки; runtime не зависит от VBA/PowerShell COM, пакет устанавливается воспроизводимо, старые отчёты читаются и пользовательские документы не затронуты. Если обязательный live gate не выполнен, итог обозначается как непроверенный, а не готовый к замене текущего релиза.

## Уточнение контракта, согласованное пользователем 10.11.2026

Пользователь выбрал: «Отклонять неподдерживаемые символы до изменения документа и продолжить».
В установленном CorelDRAW 27.2.0.135 текстовые COM-методы повреждают суррогатные пары. Для нового/заменяющего текстового содержимого консервативно отклонять символы вне BMP, одиночные суррогаты UTF-16 и NUL до первой мутации (включая BeginCommandGroup), с явным кодом TEXT_UNSUPPORTED_CHARACTER / TEXT_INVALID_UTF16 и applied:false. Не заменять и не удалять символы молча. Кириллица и прочий допустимый BMP-текст остаются в контракте. Проверка emoji означает подтверждённый отказ без изменения документа; успешный round-trip emoji больше не блокирует перенос. Это правило относится к содержимому, передаваемому для изменения текста; исторические отчёты сохраняются. Если операция изменяет несколько текстовых фигур, проверить всё входное содержимое до первой мутации.
