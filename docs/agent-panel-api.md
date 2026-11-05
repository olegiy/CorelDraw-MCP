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
`read_only`, `output_root_id`, `max_calls` 1..100 (default 20).
Повтор одинакового тела с тем же ID возвращает прежний Run и frozen профиль;
другое тело даёт 409. До получения ответа исход выясняется через
`GET /requests/{client_request_id}`. Ранний
`POST /requests/{client_request_id}/stop` создаёт durable tombstone.

`POST /runs/{id}/confirm` принимает только `confirmation_id`,
`operation_digest`, `approved`. Новый код/путь не является частью approval.
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
ключ PUT принимается как секрет и сохраняется в vault. Native-tools test
`POST /profiles/{id}/test` не обращается к CorelDRAW.
`GET/PUT /workspace` задают существующие output roots.
`POST /files/register` регистрирует обычный локальный CDR и возвращает
`file_ref/display_name`, не произвольный file-read URL.

## Диагностика И Артефакты

`GET /health` и `/metrics` возвращают cached/aggregate state без COM.
`POST /diagnostics` создаёт manual Run с allowlist JSON.
`GET /artifacts/{artifact_id}` выдаёт только зарегистрированный проверенный
экспорт/JSON как attachment. `GET /images/{image_id}` выдаёт зарегистрированный
PNG до 10 МиБ/2400 px. Query `path` запрещён.

`POST /recovery/review` записывает операторское наблюдение отдельно от исхода
старой операции; `/recovery/reviews` читает журнал. Текущий record-only review
не снимает safety barrier при непроверенном владельце или незавершённом Job.

`POST /recovery/reconcile` принимает пустой объект и доступен только при
отключённом MCP и сохранённых pending bindings. Host читает исходные отчёты
из фиксированного Corel runs root после доказанного завершения прежнего
владельца. Старый модельный план не возобновляется; safety barrier не снимается
одной записью review. Пути отчётов от клиента не принимаются.

`POST /history/backup` принимает пустой объект и создаёт согласованную копию
SQLite через native backup API внутри каталога `backups` панели. Ответ не
содержит локальный путь. DB/WAL не копируются как несогласованная пара файлов.
