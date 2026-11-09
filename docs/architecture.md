# Архитектура

Codex общается по stdio с TypeScript MCP-сервером. `src/tools.ts` задаёт строгие схемы Zod, `src/server.ts` регистрирует 18 инструментов и семь локальных ресурсов справки. В stdout разрешён только MCP; служебные сообщения уходят в stderr.

`JobManager` атомарно сохраняет запросы и outcomes. Mutation ID строится из SHA-256 `request_id`; отдельный canonical fingerprint различает конфликт аргументов. Каталог задания — durable reservation. Потерянный владелец даёт `unknown`, автоматического replay нет. Polling читает диск и не вызывает COM. Дедлайн наблюдения не отменяет работающий COM-вызов.

`PowerShellExecutor` запускает один скрытый Windows PowerShell 5.1 `-STA`. Параметры передаются массивом аргументов и UTF-8 JSON-lines, без shell evaluation. Один именованный mutex `Local\CorelDrawMcp_v27` охватывает подключение, операции и cleanup. Закрытие транспорта позволяет текущему вызову завершиться; `Application.Quit` не вызывается.

По умолчанию executor также запускает отдельный read-only процесс наблюдения
на каждый job (`CORELDRAW_MCP_VBA_OBSERVER=observe`). Он читает Win32-окна
проверенного процесса приложения без COM/VBE и не отправляет клики/закрытие.
Worker сообщает HWND после mutex/подключения, до первого VBE-вызова; отдельные
поколения охватывают подготовку VBA, Compile, Main и cleanup, включая внутренние
VBA-вызовы экспорта/измерения. Подключение к COM и получение HWND не покрыты.
`off` исключает запуск observer, получение HWND и observer-handshake.
`compile-errors` пока отклоняется при старте: проверенных action-профилей нет.

Единственный C#-читатель stdin worker передаёт записи на STA через очередь.
Observer-handshake имеет корреляцию и предел 2 с; поздние ответы пропускаются
dispatcher и не поглощают следующий job/JSON-validation. Observer контролирует
identity владельца/worker, lease с renew и собственный watchdog; его отказ
деградирует диагностику, сохраняя COM pending. Повтор мутации не выполняется.
MCP EOF закрывает приём заданий и позволяет текущему job завершиться естественно.

Node публикует только проверенную `vba_observer` schema-1 сводку: режим,
state, счётчики, фиксированный код, truncation и локальный путь журнала.
Обновления доступны через `corel_get_job` при заблокированном COM. Token,
текст окна и controls не попадают в публичный report/log. Единственный писатель
`vba-observer.jsonl` — observer; квоты: 100 событий, запись 64 КиБ, файл 8 МиБ,
текст диалога 16 КиБ UTF-8. Сканирование ограничено 1 с, чтение control — 250 мс.
Перед окончательным result executor останавливает observer и ждёт выхода,
чтобы журнал завершённого job не менялся во время inventory/prune. Компактизация
и диагностический экспорт сохраняют безопасную сводку без пути журнала.
Ни обнаружение окна, ни его исчезновение не определяют outcome задания.

`document_id` включает UUID сессии worker и UUID документа. Worker сопоставляет IUnknown identity с открытыми документами; закрытые документы удаляются из registry, reconnect меняет сессию. Для объектов используется `Shape.StaticID`, а не порядковый индекс.

Регистрация всегда публикует полный набор инструментов. Старый
`%LOCALAPPDATA%\CorelDrawMcp\config.json` не читается и не меняется.
Новые reports не содержат разрешительного `tool_policy`; исторические payload
с этим полем сохраняются при чтении и компактизации. Подсказки MCP описывают
побочные действия, но не требуют подтверждения внутри сервера.

## VBA и экспорт

Harness получает точный `Document` параметром RunMacro, активирует и проверяет
его внутри VBA до Main. После Main worker проверяет identity активного документа
и возвращает `TARGET_DOCUMENT_CHANGED` при смене. Это не sandbox: произвольный
код может менять другие документы и возвращаться обратно; такие побочные
действия нельзя предотвратить проверкой ActiveDocument. Для стандартных recipes
ошибки с Source `CorelMcpRecipe` содержат JSON: applied, error_code,
message, cleanup_failed. applied=true консервативно означает возможное действие;
cleanup_failed переводится в независимую ошибку cleanup `recipe.selection`.

Snapshot `.bas` читается как UTF-8, `Attribute VB_*` удаляется только в snapshot, код загружается через `CodeModule.AddFromString`. Worker создаёт уникальный временный GMS и выбирает его VBProject. Compile command 578 применяется к нему; `compiler_location` берётся из реально выделенного error span. `Main` возвращает JSON через typed harness; Node проверяет JSON до выгрузки GMS. Массивы и null передаются как raw JSON без PowerShell-unwrapping.

Cleanup независимо восстанавливает настройки документа, выделение, Undo, оптимизацию, события, страницу и активный документ. Primary error сохраняется отдельно от `cleanup.errors`. `succeeded` плюс `cleanup.completed=false` даёт MCP `isError=true`, сохраняя outcome; это не повод повторять мутацию.

Экспорт использует staging-файл рядом с назначением; публикация с заменой существующего файла происходит после успешного Finish. Нативный SaveAs использует `StructSaveAsOptions.Overwrite=true`, без обещания атомарности. PDF использует PublishToPDF с восстановлением PublishRange/PageRange. Прочие форматы используют типизированный VBA bridge из-за late-bound COM optional parameters. Для AI/EPS text curves и DXF/PLT создаётся собственный временный документ, копии проверяются на geometry invariant. Внешние CDR/экспорты не являются служебными артефактами для prune.

Панель принимает прямые абсолютные пути и хранит `output_directory` только как
назначение по умолчанию. Run фиксирует профиль, credential revision, документ,
аргументы и operation/scope digests; перед dispatch повторно проверяются их
идентичность, Stop, текущий барьер и бюджет с postchecks. Нет пользовательского
read-only/consent/approval. Legacy schema-1 декодируется только для чтения;
неотправленное ожидание старого подтверждения становится `interrupted`.
Readiness снимает только текущий глобальный барьер по неизменённым ID/evidence
и проверенному владельцу, не переписывая исход старого Run и не replay-ая его.

## Чтение API и измерение

`ApiMetadata.cs` получает `ITypeInfo` только через `IDispatch.GetTypeInfo`, затем содержащую typelib. Он читает типы, descriptors, default incoming interface coclass, aliases, inherited interfaces, enum constants и параметры функций; `Invoke` и произвольных getters в этом пути нет. Кеш индексируется GUID/major/minor/LCID и живёт в worker-модуле. Дескрипторы освобождаются в finally; Application не освобождается модулем.

Измерение создаёт изолированную копию в собственном временном документе, проверяет геометрию и получает line/cubic segments. TypeScript выполняет de Casteljau flattening, convex hull и rotating calipers. Лимит 200000 точек; `boundary_error_mm=tolerance_mm`, `dimension_error_mm≤2*tolerance_mm`. Неподдерживаемые эффекты завершаются явной ошибкой, частичного успеха нет. Исходный документ не должен менять Dirty, selection и history.

## Локальные операции

Guide и ресурсы читают одни Markdown-файлы без COM, в том числе пока worker занят. Notes атомарно сохраняются в `root/notes` как непроверенные данные; они не исполняются и не изменяют скилл.

Diagnostics строится по allowlist: версии/время/статус/коды/cleanup и хешированный diagnostic ID. Исходный request ID, пользовательские paths/names/code/text/result и raw errors исключаются. Публикация атомарна и требует внешний путь.

Artifact inventory считает SHA-256 файлов, bytes и fingerprint. Protected outcomes не очищаются. Prune повторно проверяет токен и блокирует `.prune-lock`, затем сначала сохраняет компактный durable report (`details_pruned=true`, identity/fingerprint/outcome/error codes), после чего удаляет внутренние файлы. Reservation остаётся, повтор request ID не выполняет действие заново; при ошибке удаления возможна частичная очистка, итог сообщает результат каждого кандидата. Для отката подробностей автоматического восстановления нет: удаление явно запрошено. Junction/reparse traversal запрещён.

## Установка

Скилл хранится в репозитории и доступен глобально через Windows junction. Установщик делегирует конфигурацию `codex mcp`, сохраняет резервную копию config и отказывается перезаписывать чужие имена. Перенос проекта требует явной новой регистрации. Регистрация и переустановка не выполняются при обычной проверке документации.
