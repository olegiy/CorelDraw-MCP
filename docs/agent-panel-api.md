# HTTP API Панели

Контракт версии 1 имеет префикс `/api/v1`. Источник типов: Pydantic DTO в
`webhost/corel_agent/contracts.py`. Offline схема `webhost/openapi.json`
получается командой `.venv\Scripts\python.exe -m corel_agent.export_openapi`
из `webhost`; `--check` проверяет дрейф без lifespan/MCP. В `webui` команды
`npm run generate:api` и `npm run check:api` генерируют/проверяют
`src/api.generated.d.ts` через отдельный pinned codegen workspace.

## Доступ

`POST /access/bootstrap` принимает секрет только заголовком
`X-Corel-Bootstrap`, выдаёт сессионную cookie и CSRF response.
`GET /access/session` восстанавливает CSRF по действующей cookie.
Все API endpoints, SSE и артефакты требуют сессии. Изменяющим запросам нужны
точный Origin и `X-Corel-CSRF`. Тело ограничено 2 МиБ фактически прочитанных
байтов, сообщение 64 КиБ UTF-8. Ошибки имеют `code`, русское `message`,
`trace_id`, `retryable`; input/credentials в ошибки не включаются.

## Команды

`POST /runs` создаёт агентный Run, `POST /actions` manual Run без модели.
Ответ 202 выдаётся после durable acceptance. Команда включает UUID
`client_request_id`, `conversation_id`, выбранные `profile_id`, `document_id`,
`new_document` как выбор новой цели, необязательный `output_directory` и
`max_calls` 1..100 (default 20). Существующий документ и `new_document=true`
одновременно не допускаются. Без явно переданного каталога используется
настройка workspace; эффективное назначение фиксируется в Run.
Повтор одинакового тела с тем же ID возвращает прежний Run и frozen профиль;
другое тело даёт 409. До получения ответа исход выясняется через
`GET /requests/{client_request_id}`. Ранний
`POST /requests/{client_request_id}/stop` создаёт durable tombstone.

Команды исполняются сразу после проверки. Confirm/register/review endpoints
удалены (404), удалённые поля новых DTO отклоняются (422), а не игнорируются.
`POST /runs/{id}/stop` не отменяет уже отправленный COM.

## Чтение

`GET /runs`, `/runs/{id}`, `/runs/{id}/operations`, `/operations/{id}/report`,
`/conversations`, `/conversations/{id}/messages` не вызывают модель/COM.
Pagination default 100/max 500. Snapshot содержит `last_seq`, не весь журнал.
SSE `/runs/{id}/events` использует `event:update`, `id:seq`, heartbeat 2 с;
`Last-Event-ID` имеет приоритет над `after`. Отдельный `/event-log` позволяет
восстановить события страницами. Дубликаты игнорируются; gap/неизвестная схема
вызывают чтение snapshot, не повтор команды.

## Операторские Настройки

`GET/PUT /profiles/{id}` и `GET /profiles` передают метаданные без credentials;
ключ PUT принимается как секрет и сохраняется в vault. Профиль не содержит
consent/capability flags; проверенный PNG автоматически включается в запрос
вместе с контекстом. `POST /profiles/{id}/test` использует синтетический PNG и
native tool probe без CorelDRAW, документов и текстового fallback.
`GET/PUT /workspace` задают `output_directory: str | None`, не allowlist.
CDR открывается по прямому абсолютному Windows-пути; Save As/export заменяют
существующий файл без `overwrite` input. Download остаётся доступным только
по зарегистрированному artifact ID с проверкой digest/типа/размера.

## Диагностика И Артефакты

`GET /health` и `/metrics` возвращают cached/aggregate state без COM.
Health включает `readiness_reason`; проверка ledger/owner может атомарно снять
доказанно устранённый текущий глобальный барьер, но не вызывает модель/MCP tools.
`POST /diagnostics` создаёт manual Run с allowlist JSON.
`GET /artifacts/{artifact_id}` выдаёт только зарегистрированный проверенный
экспорт/JSON как attachment. `GET /images/{image_id}` выдаёт зарегистрированный
PNG до 10 МиБ/2400 px. Query `path` запрещён.

`GET /recovery/reviews` читает только исторический аудит: `id`, `at`, opaque
`data` исходной записи. Новые reviews не записываются и не являются допуском.
Готовность определяется сервером при приёме новой изменяющей команды и после
refresh: связанные эффекты должны иметь terminal result `succeeded|failed`
и `cleanup.completed=true`, либо ledger должен доказать отсутствие dispatch.
Текущий владелец и свежий кэш документов проверяются; снятие глобального
барьера атомарно по его ID и неизменённому evidence. Старый snapshot/outcome
не переписывается. Unknown, неполный cleanup, pending Job и непроверенный owner
не обходятся пользовательским boolean или комментарием.

`POST /recovery/reconcile` принимает пустой объект и доступен только при
отключённом MCP и сохранённых pending bindings. Host читает исходные отчёты
из фиксированного Corel runs root после доказанного завершения прежнего
владельца. Старый модельный план не возобновляется и новый MCP не запускается;
сверка не подменяет readiness. Пути отчётов от клиента не принимаются.

`POST /history/backup` принимает пустой объект и создаёт согласованную копию
SQLite через native backup API внутри каталога `backups` панели. Ответ не
содержит локальный путь. DB/WAL не копируются как несогласованная пара файлов.
