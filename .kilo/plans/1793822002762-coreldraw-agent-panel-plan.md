# План внедрения панели управления агентом CorelDRAW

> Для исполнителя: применять `executing-plans` либо `subagent-driven-development`; выполнять задачи по контрольным точкам ниже. Исходники и окружение не изменены при подготовке этого плана.

**Goal:** отдельная локальная панель показывает запросы, действия агента, результаты и превью; создание и редактирование графики выполняются только в CorelDRAW 27.

**Architecture:** React/Vite и независимые блоки CAD-Agent-Next адаптируются в отдельное приложение. FastAPI web-host владеет агентным циклом, историей и единственным MCP-подключением; Corel-адаптер вызывает существующий TypeScript MCP через stdio. Цепочка PowerShell 5.1 STA -> COM/VBA и публичные MCP-контракты остаются источником истины.

**Tech Stack:** Windows с интерактивной сессией, CorelDRAW 27 + VBA, Node.js 24, Python 3.12, React 19, Vite, TypeScript, FastAPI, httpx, официальный Python MCP SDK, sse-starlette, Pydantic, sqlite3, Windows Credential Manager, pytest, Playwright.

**Spec:** концепция и требования пользователя из текущего обсуждения; решения подтверждены через вопросы о запуске агента, доступе, подтверждениях, истории, фоне и границе MVP. Отдельный design-файл не требуется: согласованные решения зафиксированы здесь.

## 1. Согласованные решения

- Собственный агентный цикл с OpenAI-compatible API; интеграция с интерактивными сессиями Codex/Kilo не входит в MVP.
- Web-host на FastAPI/Python; переносимый UI на React/Vite. Python не выполняет CorelDRAW COM и не заменяет существующий исполнитель.
- Только loopback и один оператор в той же Windows-сессии. Учётные записи, удалённые пользователи, LAN, HTTPS-шлюз и многопользовательские роли исключены.
- Подтверждаются только опасные действия. Любой `corel_execute_vba`, включая стандартный рецепт, считается опасным; постоянного разрешения VBA в MVP нет.
- История, события и связи идентификаторов автоматически сохраняются локально. API-ключи не попадают в это хранилище.
- Агент продолжает обычные разрешённые действия в фоне после закрытия вкладки. На опасном действии ждёт подтверждения; неизвестный исход запрещает дальнейшие изменения.
- Первый пользовательский выпуск: рабочий чат, одно модельное подключение, точный документ, инспекция, VBA, превью, сохранение CDR, экспорт PNG/SVG/PDF, фон, остановка, история и диагностика.
- Read-only вертикальный срез является промежуточной контрольной точкой, а не отдельной заменой согласованного MVP.

## 2. Общие ограничения

- Не переносить `solidworks_mcp_server.py`, COM/Python-исполнитель, mesh, SolidWorks-проверки и весь `cadnext/agent.py`.
- Не изменять донорский репозиторий `C:\Users\olegiy\CAD-Agent-Next`. Копировать выбранные исходники с указанием происхождения и сохранением MIT notice.
- Не переносить чужие настройки, ключи, логи, `.venv`, `node_modules`, сборки и пользовательские данные.
- Сохранить Node.js `>=24 <25`, MCP server/client `2.4.0`, существующие строгие схемы и 18 инструментов. Не переводить Corel jobs на experimental MCP Tasks.
- Все Corel-вызовы проходят через один координатор; COM сериализуется существующим `Local\CorelDrawMcp_v27`.
- Polling, SSE, просмотр истории, health и выдача сохранённых изображений не выполняют COM.
- `request_id`, `document_id`, `Shape.StaticID`, primary error, `cleanup.errors`, `unknown` и `timed_out` не теряются при нормализации.
- Не выполнять автоматический повтор мутации, исход которой неизвестен, и не повторять VBA автоматически после частичного отказа.
- Не вызывать `Application.Quit`; не завершать процессы по имени, порту или непроверенному PID; не передавать пользовательские аргументы через shell/`Invoke-Expression`.
- Одновременное агентное управление через панель и Codex/Kilo не поддерживается в MVP. Существующая регистрация Codex сохраняется; внешний `BUSY` не обходится.
- Ручное редактирование допустимо между операциями; одновременное редактирование документа во время выполняющегося COM/VBA не является поддерживаемым сценарием.
- Все пользовательские тексты и документация панели на русском. Идентификаторы и протоколы остаются техническими.
- Коммиты, push и установка в пользовательское окружение выполняются только по отдельному разрешению; этот план сам по себе не разрешает такие действия.

## Review Focus

- Повторный HTTP-запрос после изменения профиля возвращает первоначальный Run и его snapshot, а не новый запуск с новым ключом/моделью: T1/T4/T5.
- Закрытие вкладки, отмена SSE и медленный подписчик не отменяют agent task/MCP; Stop во время COM только запрещает следующие шаги: T0/T4/T6.
- Approval с изменившимся кодом, документом или файлом, а также два одновременных подтверждения не запускают действие: T3/T7.
- Crash между отправкой мутации и получением job_id, повреждённая БД и незавершённый прежний владелец не приводят к replay или второму исполнителю: T0/T1/T8.
- Подменённые Host/Origin, raw validation errors с ключом, path aliases в VBA и PNG неверного типа не превращаются в доступ к файлам/утечку/ложный успех: T2/T3/T7.

## 3. Основания и граница заимствования

**Текущий проект:** `src/index.ts`, `src/server.ts:60-156`, `src/jobs.ts`, `src/types.ts`, `src/tools.ts`, `src/guides.ts`, `docs/architecture.md` и `test/fixture-server.mjs`.

**Донор:** `ui/src/MessageContent.tsx`, `ActivityIndicator.tsx`, `ModelPicker.tsx`, `styles.css`, `api.ts`, `runEvents.ts`; независимые части `cadnext/provider.py`, `settings.py`, `streaming.py`, `launcher.py`; `docs/app.md`, `docs/agent.md`, `LICENSE`, `UPSTREAM.md`.

- `App.tsx` донора содержит много несвязанных функций. Перенести композицию экрана и выбранные компоненты, но не сохранить его монолитную структуру.
- `cadnext/mcp.py` возвращает непосредственный результат SolidWorks. Вместо его нормализатора создать Corel-адаптер с ожиданием Job и сохранением отчёта.
- В доноре закрытие SSE вызывает `agent.stop()` (`cadnext/app.py:763-781`), а `pagehide` посылает stop (`ui/src/App.tsx:592-596,670-674`). Обе автоматические остановки исключить; явная кнопка Stop остаётся.
- `privacy.py` содержит `execute_python` и восстановление Python-литералов; `context.py` содержит свёртку CAD-деревьев. Не переносить их как готовую защиту VBA или проверку CorelDRAW.
- Python MCP SDK 1.30.0 имеет `PROCESS_TERMINATION_TIMEOUT=2.0`; его Windows process context также требует проверки поведения при отмене. Обычный выход SDK нельзя считать безопасным shutdown длительного COM.

## 4. Целевая структура

```text
Браузер: webui, без редактирования CDR и прямого MCP/COM
  -> HTTP JSON /api/v1 + SSE
FastAPI web-host: авторизация локальной сессии, API, история, события
  -> Runtime: модель -> предложение -> политика -> выполнение -> результат
  -> CorelMcp: официальный ClientSession, один owned stdio transport
  -> node <корень проекта>/dist/index.js
  -> существующий JobManager -> PowerShell STA -> CorelDRAW 27
```

| Компонент | Планируемые файлы | Ответственность |
| --- | --- | --- |
| HTTP-контракты | `webhost/corel_agent/contracts.py`, `webhost/openapi.json` | Pydantic DTO, enum, ограничения, offline OpenAPI |
| Хранилище | `webhost/corel_agent/store.py`, `assets.py` | SQLite, транзакции, идемпотентность, события, PNG по непрозрачным ID |
| Процессы и MCP | `webhost/corel_agent/owned_stdio.py`, `corel_mcp.py`, `launcher.py` | Владение, SDK, shutdown, каталог, Job polling |
| Агент | `webhost/corel_agent/runtime.py` | Один активный Run, последовательность вызовов, бюджет, фон, Stop |
| Политика | `webhost/corel_agent/policy.py`, `privacy.py` | Привязка документа/путей, опасные действия, подтверждения, внешний контекст |
| Модель и настройки | `webhost/corel_agent/provider.py`, `streaming.py`, `settings.py` | Один профиль MVP, native tools, секреты, неизменяемый snapshot |
| API и доступ | `webhost/corel_agent/app.py`, `access.py`, `events.py` | Тонкие handlers, cookie/CSRF, ограничения, SSE подписки |
| Наблюдаемость | `webhost/corel_agent/observability.py` | Санитизированные JSONL, метрики, health, техническая диагностика |
| Экран | `webui/src/App.tsx`, `components/Chat.tsx`, `DocumentPanel.tsx`, `RunPanel.tsx`, `ApprovalDialog.tsx`, `PreviewPanel.tsx`, `SettingsDialog.tsx` | Композиция, история, документ, операции, превью, настройки |
| Клиент | `webui/src/api.ts`, `api.generated.d.ts`, `runEvents.ts`, `useRunStream.ts`, `styles.css` | API, восстановление SSE, отображение фактических состояний |
| Установка | `scripts/Install-CorelAgent.ps1`, `Start-CorelAgent.ps1`, `Stop-CorelAgent.ps1` | Отдельные install/start/stop без изменения регистрации Codex |
| Проверки | `webhost/tests/`, `webui/tests/`, `scripts/Verify-CorelAgent.ps1` | Фиктивные модель/MCP, браузерные и opt-in live проверки |

В последующих задачах сокращённые Python-имена относятся к `webhost/corel_agent/`, Python test-имена к `webhost/tests/`; UI-компоненты `Chat.tsx`, `DocumentPanel.tsx`, `RunPanel.tsx`, `ApprovalDialog.tsx`, `PreviewPanel.tsx`, `SettingsDialog.tsx` находятся в `webui/src/components/`.

Профили без ключей, `history.sqlite3`, `assets/`, `logs/` и `runtime.json` находятся в `%LOCALAPPDATA%\CorelDrawAgent`. Секреты: Windows Credential Manager, namespace `coreldraw_agent`, отдельно от донора. Corel jobs остаются в существующем `%LOCALAPPDATA%\CorelDrawMcp\runs`; менять формат или автоматически очищать это хранилище нельзя.

## 5. Термины и состояния

| Идентификатор | Значение и срок действия |
| --- | --- |
| `conversation_id` | Локальная переписка; сохраняется между запусками, не является Corel-документом |
| `run_id` | Один агентный или ручной прогон; только один незавершённый прогон на web-host |
| `client_request_id` | UUID HTTP-команды из браузера; повтор с тем же телом возвращает прежний Run, с другим телом даёт 409 |
| `operation_id` | Созданный сервером ID одного предложенного MCP-вызова, включая проверки |
| `corel_request_id` | `web:<operation_id>` для MCP-схем с `request_id`; сохраняется до отправки, не предоставляется моделью |
| `job_id` | ID из фактического ответа Corel MCP; до получения ответа может отсутствовать |
| `document_id` | ID документа текущей сессии PowerShell worker; после reconnect не переносится автоматически |
| `confirmation_id` | Одно подтверждение операции, связанное с digest параметров, кода, документа и путей |
| `file_ref` / `image_id` | Непрозрачная ссылка на разрешённый файл/сохранённый PNG; не произвольный системный путь |

`Run.kind = agent | manual`. Manual Run используется для кнопок инспекции/превью/сохранения/экспорта и не обращается к модели, но проходит ту же политику, журнал и координатор.

`Run.status`: `preparing`, `running`, `awaiting_confirmation`, `stopping`, `completed`, `stopped`, `failed`, `uncertain`, `limit_reached`, `rejected`, `halted`, `interrupted`. Последние восемь, начиная с `completed`, терминальны; `awaiting_confirmation` не удерживает COM mutex, но резервирует агентный Run. Статус выполнения не является доказательством достижения пользовательской цели.

| Состояние Corel Job | Обработка панелью |
| --- | --- |
| `running` | Сохранять `job_id`/stage; ждать через `corel_get_job`; новые COM-вызовы не запускать |
| `timed_out` | Показать истечение срока наблюдения; продолжать чтение известного Job; операция не отменена |
| `succeeded`, cleanup успешен | Сохранить результат, выполнить зарезервированную проверку при необходимости |
| `succeeded`, cleanup неполон | Показать «Действие выполнено, восстановление не завершено»; Run `halted`, дальнейшие изменения заблокированы |
| `failed` | Сохранить primary error, applied/cleanup; Run `failed`, без автоматического repair или повтора VBA |
| `unknown` либо потеря связи при отправке | Run `uncertain`; сохранить барьер безопасности, не возобновлять мутации после перезапуска |

Известный Job может позднее перейти из `timed_out` в окончательное состояние. `monitoring_state = healthy | degraded` хранится отдельно: ошибка read-only polling не доказывает отказ или неизвестный исход самой операции.

## 6. Потоки данных

1. **Запуск приложения.** Launcher получает singleton-владение и резервирует `127.0.0.1:8878` до запуска MCP. Host открывает/проверяет хранилище, проходит recovery и поднимает один owned transport. MCP `tools/list` и `corel_guide` загружаются с pagination; руководство служит доверенной инструкцией для агента. Подключение/обновление Corel status выполняется явно, не через HTTP health.
2. **Запрос.** Browser создаёт `client_request_id`; POST валидируется. До запуска фоновой задачи SQLite атомарно резервирует Run, fingerprint и первый event. Дубликат не создаёт задачу повторно. Если HTTP-ответ потерян, browser читает `/requests/{id}` или историю, а не посылает новую мутацию.
3. **Модель.** Runtime фиксирует профиль и целевой документ, строит разрешённый каталог native tools, отправляет подготовленный контекст. Системные поля `request_id`, `document_id` и deadline добавляет host; их попытка подмены моделью отклоняется. Ответ модели не считается выполненным действием.
4. **Операция.** Policy разрешает обычное действие либо создаёт approval. После разрешения операция и её параметры сохраняются, затем статус `dispatching` фиксируется до передачи по stdio. Адаптер получает Job, немедленно сохраняет `job_id` и опрашивает только `corel_get_job`. При `timed_out` интервалы polling ограничены, без tight loop.
5. **Результат и превью.** Сохраняются полный локальный Corel-отчёт и отдельно техническая санитизированная сводка. Для `corel_preview` изображение запрашивается через `corel_get_job(include_preview=true)`; PNG проверяется, атомарно сохраняется, после этого публикуется `image` event. Путь из MCP-отчёта не становится URL чтения файлов.
6. **Фон и SSE.** Run/task принадлежат lifespan web-host, не request/SSE generator и не FastAPI BackgroundTasks. Disconnect уничтожает только подписку; обновление страницы получает snapshot и события после `last_seq`. Опасная операция без открытого браузера остаётся `awaiting_confirmation` и не исполняется.
7. **Остановка.** Только явный Stop устанавливает durable `stop_requested`; ранний Stop по `client_request_id` создаёт tombstone даже до принятия POST. Следующие модельные/изменяющие шаги запрещены; уже отправленный COM заканчивается и сохраняется. Бюджет/Stop не отменяют запись фактического результата.
8. **Shutdown/crash.** Graceful stop прекращает следующие шаги, ждёт отправленный Job и естественный выход owned MCP, после чего освобождает владение. После crash восстанавливаются отчёты, но не агентные планы. Известные Job читаются без повторной отправки; `dispatching` без `job_id` остаётся неопределённым, ID не угадывается из внутреннего алгоритма Corel.

## 7. MVP и последующие итерации

**MVP:** один настраиваемый профиль; текстовый чат/native tools; статус/документ/страница/выделение; ограниченная инспекция с указанием truncation; создание документа; открытие явно выбранного CDR; VBA после подтверждения; PNG-превью page/selection; обычное сохранение выбранного CDR; PNG/SVG/PDF в разрешённое место; подтверждение перезаписи; фон; Stop; локальная история; просмотр cleanup/unknown; техническая диагностика.

- Новая переписка не закрывает рисунки. Смена документа запрещена для текущего Run; следующий Run может иметь новую привязку.
- Один Run работает с одним целевым документом. При запросе создания нового документа его ID фиксируется по результату первой операции; без выбора документа или запроса создания агент уточняет цель.
- `corel_get_job` служебный для адаптера, не модельный инструмент. Для MVP модельный каталог ограничен перечисленными возможностями и фактически зарегистрированными Corel tools; новые неизвестные tools не разрешаются автоматически.
- Отдельные read-only кнопки используют координатор; при занятом Job возвращаются cached данные с отметкой времени или `BUSY`, а не скрытый параллельный COM.

| Итерация | Содержание | Условие начала |
| --- | --- | --- |
| I1: удобство | PNG/JPEG/WebP вложения с явной Vision-настройкой; несколько профилей и каталог моделей; JPEG/TIFF/AI/EPS/DXF/PLT UI; расширенная инспекция и управление историей | MVP прошёл safety и live gates; текущие MCP-схемы не заменяются универсальным словарём |
| I2: рабочие сценарии | Каталог проверенных VBA-рецептов, сохранение/импорт sidecar, сравнительные превью, измерения и проверки результата | Есть тесты привязки document/StaticID и доказательства для конкретных сценариев |
| I3: общее ядро | Выделение реально общих UI/provider/event блоков для CorelDRAW и CAD-Agent-Next | Оба приложения имеют устойчивые контракты и независимые тесты |

Mesh/3D, браузерное редактирование CDR, универсальный CAD-framework, автоповтор исправленного кода, удалённый доступ, multi-user, мобильное управление по LAN и постоянное разрешение VBA не входят в этот план. Их добавление требует отдельного решения и security review.

## 8. Интерфейс и API

**Экран:** слева история и выбор целевого документа; в центре чат/composer; справа либо в раскрываемой панели операции и превью. На узком окне панели превращаются в переключаемые секции. Сохранить визуальный язык донора, без нового canvas-редактора.

- Всегда показывать выбранный документ, текущую страницу, stale/cache отметку и состояние подключения; ActiveDocument Corel и выбранная цель панели не смешиваются.
- Карточка операции: имя, параметры, stage, job_id, исход, applied, cleanup, предупреждения; подробный код раскрывается отдельно.
- Approval показывает весь VBA, документ, эффект, путь/перезапись и предупреждение о доступе к CorelDRAW/ОС. Никаких обрезанных code previews как единственного основания согласия.
- «Стоп» означает запрет следующих шагов; текст при ожидании COM не обещает немедленную отмену или Undo.
- Баннеры `timed_out`, `unknown` и cleanup отличаются. Потеря браузерного соединения показывает «Агент продолжает работу в фоне», а не «Задание остановлено».
- Превью имеет document_id/page/target/время создания; не выдаётся за live-canvas или доказательство точности векторной геометрии.
- Keyboard navigation, видимый focus, доступные диалоги, Escape без случайного approval, Enter/Shift+Enter, ширина 390 px, отсутствие remote Markdown images/HTML исполнения.

**API:** новый versioned `/api/v1`; публичного anonymous управления нет. HTTP API не является дополнительным MCP transport.

| Endpoint | Контракт |
| --- | --- |
| `POST /access/bootstrap`, `GET /access/session` | Одноразовый локальный вход, cookie и CSRF; второй endpoint требует cookie |
| `GET /health`, `GET /metrics` | Авторизованные cached состояние и технические счётчики, без COM |
| `GET /profiles`, `PUT /profiles/{id}`, `POST /profiles/{id}/test` | Один профиль MVP; метаданные без ключа; native tools test без Corel |
| `GET /workspace`, `PUT /workspace`, `POST /files/register` | Только оператор задаёт output roots и регистрирует конкретный локальный CDR; возвращается file_ref, модель эти endpoints не вызывает |
| `GET /documents`, `POST /documents/refresh` | Cached список; явный refresh создаёт manual Run и возвращает 202 |
| `POST /conversations`, `GET /conversations`, `GET /conversations/{id}/messages` | Создание/история с pagination, без открытия/закрытия CDR |
| `POST /runs` | Агентный Run; 202 и snapshot после durable acceptance |
| `POST /actions` | Manual Run для фиксированных UI-действий, без LLM, тот же idempotency/policy |
| `GET /requests/{client_request_id}`, `POST /requests/{client_request_id}/stop` | Разрешение потерянного HTTP-ответа и durable ранний Stop |
| `GET /runs`, `GET /runs/{id}`, `POST /runs/{id}/stop` | Список/snapshot/явная остановка |
| `POST /runs/{id}/confirm` | `confirmation_id`, `operation_digest`, `approved`; одноразовый атомарный переход |
| `GET /runs/{id}/events`, `GET /runs/{id}/event-log` | SSE и отдельный paginated журнал, без вызовов модели/MCP |
| `GET /runs/{id}/operations`, `GET /operations/{id}/report` | Карточки и авторизованный локальный отчёт |
| `GET /images/{image_id}` | Только зарегистрированный PNG внутри assets, без query-параметра path |
| `POST /diagnostics` | Manual Run создаёт allowlist-диагностику; download по зарегистрированному artifact ID |
| `GET /artifacts/{artifact_id}` | Только зарегистрированный диагностический JSON/экспорт в разрешённом root; без произвольного path |
| `POST /recovery/review` | Явное операторское рассмотрение барьера; не повторяет и не меняет outcome старой операции |

`POST /runs`: `client_request_id: UUID`, `conversation_id: UUID`, `profile_id: UUID`, `document_id: string|null`, `message: string`, `max_calls: int=20`. Manual action имеет тот же client_request_id/conversation_id, `tool` и валидированные `arguments`; прямого произвольного shell/HTTP/file-read инструмента нет.

Оба варианта команды фиксируют `read_only: bool` и `output_root_id: UUID|null`. POST `/files/register` принимает `{path: string}` только от авторизованного оператора, проверяет обычный `.cdr` без reparse/UNC и возвращает `{file_ref, display_name}`. PUT `/workspace` сохраняет ID и канонические пути явно выбранных локальных каталогов; их создание не выполняется скрыто. Model-facing path принимает только выданный file_ref либо `@output/<валидное имя>` внутри выбранного root; полный абсолютный путь подставляется только в структурный аргумент MCP, не в VBA-код.

`RunSnapshot`: ID, kind, status, profile snapshot без секрета, document binding, last_seq, stop_requested, pending confirmation, active_operation_id/job_id, counters, monitoring_state, safety_barrier. Messages/events/operations выдаются страницами, не безграничным массивом в snapshot.

`RunEvent`: `schema_version=1`, `run_id`, монотонный `seq`, `at` UTC, `type`, типизированный `data`, компактный `state`. Типы: `message_delta`, `message`, `tool_proposed`, `confirmation`, `tool_start`, `job_progress`, `tool_result`, `image`, `status`, `warning`, `error`. События фиксируются до публикации.

- SSE сохраняет донорские `event:update`, `id:<seq>`, `heartbeat`; `Last-Event-ID` имеет приоритет над `after`. Дубликаты игнорируются; gap или неизвестная схема приводят к snapshot recovery, не к исполнению действий.
- `runEvents.ts` использует явный `last_seq`, а не длину загруженного массива событий. Snapshot и watermark читаются в согласованной транзакции.
- HTTP: 401 нет локальной сессии; 403 доступ/CSRF/опасность; 409 BUSY, stale binding, конфликт ID/approval; 413 размер; 422 схема; 503 недоступное/повреждённое хранилище либо blocked runtime. Ошибки имеют `code`, русское `message`, `trace_id`, `retryable` только для безопасного чтения.
- Source of truth: Pydantic и offline OpenAPI. `webui/src/api.generated.d.ts` генерируется `openapi-typescript`; check drift входит в verification. OpenAPI endpoint не публикуется anonymous.
- Проектные defaults: POST максимум 2 МиБ с проверкой фактически прочитанных байтов; message максимум 64 КиБ UTF-8; max_calls 1..100, default 20; не более 10 модельных turns; pagination default 100/max 500; SSE heartbeat 2 с, очередь подписчика 100 events.
- Все семантические MCP-вызовы, включая postchecks, входят в max_calls; `corel_get_job` polling не входит. Слоты проверки резервируются до мутации. Corel deadline по умолчанию 600 с сохраняется как срок наблюдения, а не отмены; LLM HTTP timeout 120 с/connect 10 с, без автоматического retry/fallback.
- MVP data guard: до 2 ГиБ истории/assets; предупреждение при 80%; при исчерпании нельзя принять новый изменяющий Run. Не удалять доказательства автоматически; завершение уже начатой операции и чтение её отчёта остаются приоритетом.

## 9. Политика доступа и безопасность

| Действие | Правило MVP |
| --- | --- |
| Guide, cached история/статус | Без approval, только авторизованный браузер |
| Corel inspect/preview | Без approval, точный документ, единая COM-lane, лимиты |
| Создать документ / открыть явно выбранный CDR | Обычное действие; только в рамках текущего запроса и разрешённого file_ref |
| Save выбранного CDR на его текущем пути | Обычное сохранение; выбранная цель и разрешение записи должны быть явно показаны оператору при запуске |
| Save As / export в новый разрешённый файл | Без approval при `overwrite=false`; выход за разрешённые roots запрещён |
| Перезаписать другой CDR/существующий export | Approval с конкретным путём и fingerprint существующего файла; изменение до dispatch аннулирует согласие |
| Любой VBA / закрытие с потерей изменений / закрытие чужого документа | Approval; код имеет полный доступ, документная привязка не является sandbox |
| Prune / изменение внешних файлов / неизвестный инструмент | Не предоставлять модели в MVP; дальнейшее включение только через явную политику и approval |

- ReadOnlyHint/destructiveHint являются UX-подсказками, не защитой. Host использует явную Corel-классификацию и пересечение с зарегистрированными tools. `disabled_tools` текущего MCP не меняется из панели в MVP.
- Для выбора документа оператор даёт scoped разрешение на обычные действия. Режим «Только чтение» исключает все изменения, даже если модель предлагает их. Изменения настроек не являются модельными tools.
- Bootstrap: одноразовый случайный 256-битный секрет в URL fragment, удаляемый из address bar до обмена; он не сохраняется/не логируется. POST обмена устанавливает случайную HttpOnly/SameSite=Strict session cookie; CSRF token находится только в памяти UI и восстанавливается через авторизованный session endpoint.
- Проверять точный Host с портом, Origin и Fetch Metadata; state-changing requests без ожидаемого Origin отклонять. Никаких wildcard CORS. API, SSE, изображения и диагностика требуют cookie. Cookie не является защитой от вредоносного процесса того же Windows-пользователя.
- Bind только `127.0.0.1:8878`; занятый порт приводит к отказу запуска. Production CSP: self scripts/connect/styles, self/data/blob images, frame-ancestors none, base-uri none; no-store/nosniff/no-referrer; без remote assets.
- API-ключи только Credential Manager. Недоступность vault блокирует запуск модели, без fallback в JSON/файл/env. MCP child получает ограниченный набор Windows env плюс Corel roots, но не ключи провайдера.
- Внешний provider только явно выбранный оператором; HTTPS, без userinfo/redirect/proxy environment. HTTP допустим для явно локального loopback API. Модель и содержимое CDR не могут менять base_url или credentials.
- До отправки показывать адрес модели и разрешение передачи текстового контекста/кода. Автоматическая отправка превью модели выключена по умолчанию; включается явно при Vision и фиксируется в snapshot Run. Не обещать обезличивание изображений или произвольного VBA.
- Файлы открываются по выданным file_ref; новые имена разрешаются только внутри выбранных output roots. Запретить traversal, NUL, ADS, device/reserved names, UNC и reparse/junction. Проверять назначение перед отправкой и не обещать атомарной защиты от внешнего изменения файла во время нативного Save/Finish.
- Реальные пути исключаются из модельного контекста через структурные ссылки/маскирование. Сгенерированный VBA не восстанавливается Python AST/regex-подстановками; неразрешённые path aliases внутри кода отклоняются до исполнения. Новая privacy-реализация проверяется на кавычках, Unicode и многострочном VBA.
- Approval связан с неизменяемым digest и одноразовым ID; изменение документа, пути, overwrite, кода или file fingerprint требует нового подтверждения. Конкурирующие подтверждения из двух вкладок не исполняют операцию дважды.
- POST bootstrap передаёт секрет только в `X-Corel-Bootstrap`, не query; cookie `corel_agent_session` имеет `HttpOnly`, `SameSite=Strict`, `Path=/api/v1`, без persistent срока и без `Secure` для явно выбранного HTTP loopback. CSRF передаётся в `X-Corel-CSRF`. На новом запуске host прежние cookie недействительны. `-NoBrowser` не печатает и не сохраняет bootstrap token; обычный Start к уже работающему owned host посылает защищённый Windows open-panel event, host выдаёт новый одноразовый fragment и открывает браузер.
- Обработчики Pydantic validation и provider failures не возвращают/не логируют input или сырой response body. Нельзя логировать `str(ValidationError)` с секретным полем; bootstrap/cookie/CSRF и известные credential values исключаются также из исключений.
- Каталог/очистка данных не служат file browser. Сервис не работает с повышенными правами; хранилище защищено ACL текущего пользователя. Шифрование всего локального содержания и защита от malware того же пользователя вне MVP.

## 10. Надёжность, recovery и наблюдаемость

- SQLite без ORM: versioned schema, WAL, короткие транзакции и durable commit до исполнения; DB/file I/O вне event loop. Хранить conversations, runs, messages, events, operations, request reservations/tombstones, approvals и safety barriers. Секреты в этих таблицах отсутствуют.
- Неисправная БД/миграция/disk full не создаёт новую пустую БД и не допускает мутации. После ошибки сохранения уже применённой операции сохранить known job binding, прекратить дальнейшие действия и восстановить отчёт по этому Job.
- Launcher/MCP lifecycle сохраняет PID + creation identity и spawn-pending marker до запуска. Для SDK 1.30 использовать изолированный owned-адаптер, фиксированную проверенную process factory и отключённый finite termination timeout; не редактировать site-packages. Неизвестная SDK-версия или неподдерживаемый lifecycle блокируют старт, без unsafe fallback.
- Graceful cleanup экранируется от отмены до drain текущей операции; finite Uvicorn graceful timeout не должен принудительно завершать MCP. Ожидание оператора/LLM останавливается штатно, COM-вызов не отменяется. Освободить singleton/порт только после закрытия owned transport.
- После crash отсутствие доказанного исхода сохраняет барьер между запусками. Нельзя запускать второй owned MCP при живом/неопределённом прежнем владельце. Нельзя обещать сохранность дочерних процессов при аварии Windows Job Objects/питания; такие исходы остаются unknown.
- На recovery не восстанавливать LLM task/старые approvals. Если известный Job завершился, получить отчёт и сделать Run `interrupted` либо `halted`; продолжение только новым запросом. Stale document_id требует явного повторного выбора, не поиска по имени/индексу.
- `/recovery/review` не является кнопкой «очистить unknown»: нельзя снять барьер при известном незавершённом Job или непроверенном владельце. Оператор просматривает CorelDRAW и диагностику, явно привязывает текущий документ; старый outcome остаётся unknown и рассмотрение записывается отдельно. Не проверяемые автоматически предпосылки показываются как ручные, а не как выполненные проверки.
- Технические JSONL: UTC, уровень, trace_id/run_id/operation_id/job_id, stage, code, duration и outcome. Не писать полный prompt, VBA, paths, изображения, HTTP body/Authorization или секреты в технические логи. Полные пользовательские данные существуют только в авторизованной локальной истории.
- Технические логи ротируются: 10 МиБ, до 5 файлов. Это не ротация durable ledger и не удаление Corel-артефактов. Диагностика использует allowlist по принципу `corel_export_diagnostics`, не ZIP всего каталога.
- `/health`: host/MCP connectivity, active Run/Job, DB/disk guard, safety barrier, версия контракта и время cached Corel status. `healthy` HTTP не означает успешность Corel-операции.
- `/metrics`: число Run по outcomes, LLM turns/latency, Corel operations/latency, approvals, BUSY, timed_out, unknown, cleanup failures, reconnects/slow subscribers, storage bytes/free space. Не использовать пути, prompts, run_id как бесконечные metric labels. Prometheus/облако вне MVP.
- Ограниченный subscriber queue не блокирует runtime: slow client переподключается и читает durable snapshot/event-log. Ограничения по времени heartbeat не являются оценками времени разработки.
- PNG проверяется по MIME, base64, сигнатуре и декодированным размерам: не более 10 МиБ и 2400 px по стороне для Corel preview. Недопустимый image block не публикуется как успешное превью; ошибка assets не меняет сохранённый outcome самой COM-операции.

## 11. Интерфейсы реализации

DTO `RunCommand`, `AcceptedRun`, `RunSnapshot`, `RunEvent`, `ToolDefinition`, `PreparedOperation`, `OperationContext`, `RiskDecision`, `JobUpdate`, `OperationResult`, `ProfileSnapshot`, `ProcessIdentity`, `Completion` определить в `contracts.py` до реализации потребителей.

- `AcceptedRun = {is_new: bool, snapshot: RunSnapshot}`; duplicate возвращает `is_new=false`, даже если профиль изменён или Run завершён.
- `PreparedOperation`: operation_id/run_id/tool/канонические arguments, document_id, необязательный corel_request_id, policy_version, digest, required_postchecks. Dispatch-state хранится отдельно от неизменяемых параметров.
- `OperationContext`: scoped document binding, read_only, выбранный output root и registry file refs, frozen profile/permissions. Ни один параметр безопасности не приходит из текста модели.
- `JobUpdate`: operation_id, job_id, исходный job status/stage и последний отчёт; callback сначала сохраняет binding, затем публикует событие.
- `OperationResult`: operation_id, необязательный job_id, outcome `succeeded|failed|unknown`, исходный report, applied `bool|null`, cleanup и ограниченные image blocks. Runtime сохраняет images и публикует image_id; неизвестный applied не заменяется на false.
- `Completion`: фактическое assistant message и валидированные native tool_calls с исходными ID; это не отчёт об исполнении инструмента.

| Интерфейс | Контракт |
| --- | --- |
| `Store.accept_run(command, profile_snapshot) -> AcceptedRun` | Async; atomic reservation, `is_new` и snapshot; duplicate проверяется до BUSY |
| `Store.append_event(run_id, event_type, data, state) -> RunEvent` | Async; seq и snapshot фиксируются одной транзакцией |
| `Store.record_operation(operation) -> None` | Async; pre-dispatch запись и transitions, не создаёт Corel-вызов |
| `Store.get_snapshot(run_id) -> RunSnapshot` | Async; consistent watermark, без безграничных массивов |
| `CorelMcp.start()`, `tools() -> list[ToolDefinition]`, `aclose()` | Async; один owned transport, paging каталога, безопасный exit |
| `CorelMcp.call_job(operation, emit_job_update) -> OperationResult` | Async; только PreparedOperation; сохраняемые updates и terminal report; monitoring не превращается в replay |
| `Policy.prepare(tool, arguments, context) -> PreparedOperation` | Inject binding/IDs/deadline, resolve refs, schema/path guards |
| `Policy.classify(operation, current_state) -> RiskDecision` | `allow | confirm | deny`, reasons и immutable digest |
| `Runtime.start(command) -> RunSnapshot` | Async; returns accepted snapshot, task принадлежит lifespan |
| `Runtime.confirm(run_id, confirmation_id, digest, approved) -> RunSnapshot` | Async; atomic one-shot, не принимает новый код/параметры |
| `Runtime.stop(run_id) -> RunSnapshot`, `stop_request(client_request_id)`, `aclose()` | Async; durable stop/drain; без task.cancel текущего COM |
| `Provider.complete(history, tools, emit_delta) -> Completion` | Async; native-tools validation, bounded stream, frozen profile |
| `applyRunEvent(run, event) -> RunSnapshot` | UI reducer; explicit last_seq, duplicate/gap/schema handling, без побочных действий |

## 12. Задачи внедрения

Каждая задача имеет отдельный тестируемый результат. Начинать с отрицательных тестов; запускать их до реализации и повторять после неё. Не создавать новые публичные MCP-tools ради UI.

### T0. Контракты, зависимость и технический proof

**Файлы:** создать `webhost/pyproject.toml`, `requirements.lock.txt`, `corel_agent/contracts.py`, `owned_stdio.py`, `webui/package.json`, lockfile, `webhost/tests/test_transport_compatibility.py`, `test_owned_stdio.py`; использовать существующий `test/fixture-server.mjs` без реального CorelDRAW.

- [ ] Зафиксировать donor SHA/выбранные файлы и MIT notice; выделить минимальные зависимости без numpy/scipy/trimesh/rtree. Начальные проверяемые версии: Python MCP 1.30.0, FastAPI 0.162.0, sse-starlette 3.5.1, httpx 0.28.1, Pydantic 2.14.1, keyring 25.7.0; UI версии из донорского lockfile. Генератор OpenAPI типов фиксируется в lockfile после проверки совместимости.
- [ ] Написать tests `test_node_v2_catalog_and_structured_content`, `test_long_child_is_not_killed_on_graceful_close`, `test_cancelled_subscriber_does_not_close_mcp`, `test_spawn_pending_blocks_second_owner`; expected: до реализации FAIL, после реализации PASS без COM/ключей.
- [ ] Реализовать owned process identity/SDK guard и доказать initialize/tools/list/call/get_job/image через Node 24 + существующий SDK 2.4.0. Runtime не может стать ready без этой проверки.

**Выход:** фиксированные DTO, воспроизводимый cross-language transport и безопасный lifecycle. Это обязательный gate до полноценного agent/UI.

### T1. Durable state, события и recovery

**Файлы:** `store.py`, `assets.py`, `webhost/tests/test_store.py`, `test_recovery.py`, `test_assets.py`.

- [ ] Tests: `test_duplicate_request_returns_original_run`, `test_conflicting_request_409`, `test_early_stop_tombstone`, `test_parallel_runs_one_reservation`, `test_crash_after_dispatch_no_replay`, `test_corrupt_or_full_store_blocks_mutation`.
- [ ] Реализовать Store-интерфейсы, seq/watermark, approval/operation transitions, migration/version guard и guarded assets. Хранить minimal idempotency ledger независимо от удаления пользовательской переписки в будущих итерациях.
- [ ] Проверить recovery для prepared/dispatching/known_job/terminal states и отсутствие запуска модели/мутаций. Незавершённые approvals после restart аннулируются.

**Выход:** история переживает restart; никакой Run не принимается повторно и не возобновляется скрыто.

### T2. Corel-адаптер и read-only вертикальный срез

**Файлы:** `corel_mcp.py`, начальный manual-координатор `runtime.py`, минимальные `app.py`/`events.py`, `webui/src/main.tsx`, начальная композиция `webui/src/App.tsx`, `webhost/tests/test_corel_mcp.py`, `test_manual_actions.py`; расширенный test double в `webhost/tests/fixtures/`, не в production Corel executor.

- [ ] Tests: `test_job_running_then_success`, `test_timed_out_then_success`, `test_cleanup_failure_not_retried`, `test_transport_loss_before_job_id_uncertain`, `test_poll_timeout_is_monitoring_degraded`, `test_preview_image_from_get_job`, `test_busy_read_uses_cache_or_409`.
- [ ] Реализовать call_job и manual-ветку Runtime.start с одним owned фоновым Run, immutable binding, serialized coordination, raw report preservation, bounded polling. Пока разрешены только фиксированные status/inspect/preview; агентная ветка включается в T4. `include_preview` не переиспользует произвольный path из request.
- [ ] Собрать минимальный browser-срез manual status -> выбор документа -> inspect -> page/selection preview -> SSE/report на fake MCP. Показать empty selection и stale document как ошибки, не менять цель автоматически. До T3 это только изолированный test harness, не пользовательский запуск.

**Выход:** сквозной read-only путь работает без модели и без запуска CorelDRAW в обычных tests.

### T3. Политика, локальный доступ и подтверждения

**Файлы:** `policy.py`, `privacy.py`, `access.py`, `webhost/tests/test_policy.py`, `test_access.py`, `test_approvals.py`, `test_privacy.py`.

- [ ] Tests: `test_vba_always_requires_confirmation`, `test_new_export_allowed_overwrite_confirmed`, `test_read_only_mode_denies_mutations`, `test_model_cannot_override_binding`, `test_stale_digest_or_double_confirm_rejected`, `test_origin_host_csrf_and_unauthenticated_sse_denied`.
- [ ] Реализовать bootstrap/cookie/CSRF, capabilities выбранного документа/output roots, явную таблицу рисков и one-shot approvals. Режим доступа проверяется на сервере, не только скрытием кнопок.
- [ ] Tests paths: traversal/UNC/ADS/device/junction/Unicode; tests VBA: кавычки, многострочность, unresolved aliases. Подтвердить, что privacy не использует donor restore_python и не выдаёт ложной sandbox-гарантии.

**Выход:** опасные операции невозможны без актуального согласия; обычные работают по согласованной политике.

### T4. Provider и агент в фоне

**Файлы:** `settings.py`, `provider.py`, `streaming.py`, дополнить `runtime.py` из T2, `webhost/tests/test_provider.py`, `test_runtime.py`, `test_background.py`.

- [ ] Tests: native tool name/schema/JSON/duplicate ID validation; frozen profile; credential-store failure; no fallback; budget reservation before mutation; Stop before first call/during Job/while awaiting confirmation.
- [ ] Реализовать Provider.complete и Runtime-интерфейсы, trusted corel_guide context, terminal results перед следующей модельной итерацией. Частичный/ошибочный VBA не запускается повторно и не исправляется автоматически.
- [ ] Tests: `test_disconnect_continues_safe_steps`, `test_background_danger_waits_for_confirmation`, `test_background_unknown_blocks_changes`, `test_explicit_stop_drains_job`. Task должен жить независимо от request/SSE, а не через копию donor pagehide/stop механики.

**Выход:** fake модель создаёт реальный последовательный Run с политикой/историей; закрытие браузерного соединения не меняет Run на stopped.

### T5. Полный API и OpenAPI drift check

**Файлы:** завершить `app.py`, `events.py`; создать `webhost/corel_agent/export_openapi.py`, `webhost/openapi.json`, `webui/src/api.generated.d.ts`, `webhost/tests/test_api.py`, `test_sse.py`, `test_contracts.py`.

- [ ] Tests: 202 после commit, client_request_id replay/conflict, ранний Stop, две вкладки, cookie/CSRF на каждом state-changing endpoint, body limit без Content-Length, paging, unknown/schema errors.
- [ ] Реализовать endpoints раздела 8 и SSE replay/backpressure; stream disconnect закрывает только subscriber. Snapshot может быть восстановлен без новых model/MCP calls.
- [ ] Генерировать OpenAPI offline без lifespan/MCP и TS-types локальным `openapi-typescript`. Проверка повторной генерации/`--check` не должна изменять сохранённые схемы; API fixtures валидируются обоими тестовыми слоями.

**Выход:** versioned контракт пригоден UI; без anonymous API и рассинхронизации типов.

### T6. Панель пользователя

**Файлы:** компоненты/UI-клиент из раздела 4, `webui/tests/chat.spec.ts`, `background.spec.ts`, `approvals.spec.ts`, `recovery.spec.ts`, `responsive.spec.ts`, Playwright config.

- [ ] На fake API/LLM/MCP написать failing browser tests для запроса, стадий, полной VBA-approval, Stop, selection preview, unknown/cleanup, reload и продолжения в фоне.
- [ ] Перенести стиль, Markdown, composer и выбранные компоненты; адаптировать api/reducer, убрать SolidWorks/mesh/goal и автоматические pagehide stop. Настройки MVP содержат только реально поддерживаемые функции.
- [ ] Проверить 390 px/desktop, keyboard/focus, отсутствие HTML/remote images и утечки auth/key в localStorage, историю после restart, stale document selection и две вкладки без двойного выполнения.

**Выход:** пользовательский сценарий полного MVP проходит в браузере с фиктивной моделью, без внешних ключей и CorelDRAW.

### T7. Сохранение, экспорт и наблюдаемость

**Файлы:** дополнить `assets.py`/`policy.py`/`app.py`; `observability.py`, `webhost/tests/test_save_export.py`, `test_observability.py`, `test_disk_guard.py`; browser export/report tests.

- [ ] Tests: Save текущего CDR, Save As, PNG/SVG/PDF range, отказ до approval не меняет файл, изменение file fingerprint аннулирует согласие, cleanup result сохраняется независимо от ошибки UI/assets.
- [ ] Реализовать scoped manual/agent Save/export, зарегистрированные artifacts/downloads, allowlist diagnostics, health/metrics, rotation технических логов и storage guard.
- [ ] Проверить секреты-контрольные строки во всех response/log/history/diagnostics; полные пользовательские сообщения не выдаются за обезличенную техническую сводку.

**Выход:** результаты сохраняются и доступны локально; логи не становятся вторым неконтролируемым хранилищем документов/кода.

### T8. Установка, rollout и итоговая проверка

**Файлы:** `webhost/corel_agent/__main__.py`, `launcher.py`, install/start/stop/verify scripts, `docs/agent-panel.md`, `docs/agent-panel-api.md`, optional web-раздел `README.md`, notices; `webhost/tests/test_launcher.py`, opt-in `test_live_panel.py`.

- [ ] Реализовать отдельную установку Python venv/UI и singleton launcher, порт 8878, ограниченный env, правильные пути с пробелами; запуск только без elevation. Existing Codex installer/config и donor namespace не изменяются.
- [ ] Проверить обычный запуск/остановку, NoBrowser, занятый порт, зависший Job, cancellation cleanup, crash marker, backup/versioned DB, безопасный uninstall. Uninstall удаляет только web runtime/venv/build; история, keys и Corel runs сохраняются, их удаление требует отдельного действия.
- [ ] Выполнить все gates ниже; live smoke только с временными документами. После просмотра сохранённых отчётов убрать только тестовые `CorelMcp_*` GMS, сохранить reports и не трогать сторонние проекты/рисунки.

**Выход:** MVP включается отдельным launcher; откат не требует изменения MCP или документов.

## 13. Проверки и критерии приёмки

Команды указаны для будущего исполнителя, сейчас не запускались. Python только из `webhost/.venv`; tests по умолчанию без live и внешних ключей.

```text
Корень: npm test
Корень: npm run check:contracts
webhost: .venv\Scripts\python.exe -m pytest -q -m "not live"
webhost: .venv\Scripts\python.exe -m ruff check corel_agent tests
webhost: .venv\Scripts\python.exe -m corel_agent.export_openapi --check
webui: npm run build
webui: npm run check:api
webui: npm test
Корень: scripts\Verify-CorelAgent.ps1
Live opt-in: CORELDRAW_AGENT_LIVE=1, только tests с marker live и временными документами
После live: просмотреть фактический verification report и выполнить scoped GMS cleanup
```

| Критерий | Доказательство / ответственная задача |
| --- | --- |
| MCP/PowerShell сохранили текущие контракты, 18 tools и гарантии | Existing npm tests/check:contracts; T0/T8 |
| Типизированные вызовы адресуют точный документ; штатные VBA-рецепты проверяются на двух рисунках | Fake binding tests + live two-document smoke; T2/T3/T8. Это не sandbox-гарантия для произвольного подтверждённого VBA |
| Reconnect/дубликаты POST/две вкладки не дублируют операции | Durable ledger, native call counter ровно один; T1/T5/T6 |
| Закрытый браузер не останавливает обычную фоновую работу | Browser close/reopen + fake call sequence; T4/T6 |
| Опасный фоновый шаг не исполняется без полного актуального approval | Отсутствие MCP call до подтверждения; T3/T4/T6 |
| Stop не убивает Job и не скрывает применённое действие | Slow fake executor + saved terminal report; T2/T4/T8 |
| timed_out/unknown/cleanup не превращаются в false success/retry | Переходы Job/Run, persistent barrier; T1/T2/T4/T6 |
| Refresh не запускает COM; busy-панель остаётся читаемой | Счётчик fake COM не растёт от health/SSE/history; T2/T5 |
| Preview page/selection настоящий PNG с верной привязкой | MCP image fixture + opt-in live PNG/empty selection; T2/T6/T8 |
| Перезапись другого CDR/существующего export не выполняется до согласия | Sentinel file, fingerprint change, overwrite cases; T3/T7. Обычный Save выбранного CDR проверяется отдельно |
| API-ключи не раскрываются в responses/логах/истории; bootstrap/cookie/CSRF не логируются и не хранятся в Web Storage | Canary secrets + unauthorized/Origin tests; T3/T4/T7. Session cookie и собственный CSRF response являются намеренным механизмом доступа |
| Restart восстанавливает историю, не агентный план и не старые approvals | Crash matrix, no mutation replay; T1/T8 |
| SDK shutdown не делает finite-time force kill собственного MCP | Controlled long-running child и process identity; T0/T8 |
| UI пригоден на desktop/390 px и с клавиатуры | Playwright + ручная проверка focus/dialog/contrast; T6 |
| Live проверка сохраняет пользовательские рисунки и сторонние GMS | Before/after inventory, own document IDs, scoped cleanup report; T8 |

До прохождения этих проверок не заявлять безопасность live-работы, успешное выполнение VBA или совместимость SDK только на основании screenshots/agent summaries.

## 14. Риски, зависимости и выпускные вехи

| Риск | Мера / release gate |
| --- | --- |
| Python MCP 1.30 и Node SDK 2.4 расходятся в транспорте/схеме | T0 cross-language тесты, pin версий, без experimental Tasks |
| SDK/process cancellation обрывает дочернее выполнение | Owned/shielded drain, version guard, длительный synthetic child; blocker T0 |
| В доноре слишком много SolidWorks semantics | Выборочный перенос; новые Corel contracts/policy/runtime, не копия agent.py |
| Фон без оператора выполняет нежелательные действия | Scoped ordinary permissions, immutable target, approval на VBA/overwrite, бюджеты, explicit Stop |
| Ручное редактирование/внешний MCP конкурирует с агентом | Явный busy state и operational ограничения; mutex не является блокировкой GUI |
| VBA меняет чужие документы или ОС | Полный код и предупреждение до согласия; не обещать sandbox/полную path isolation |
| Коррупция/диск/неизвестный исход теряют сведения о мутации | Commit до dispatch, known Job recovery, persisted barrier, запрет автоматического сброса |
| Маскирование ломает VBA либо раскрывает секрет | Структурные file refs, отсутствие Python literal restoration, canary/round-trip tests |
| История и изображения растут, API/SSE перегружаются | Pagination, bounded subscribers, quota и явная очистка в I1; no evidence auto-prune |
| Дополнительный Python усложняет установку | Отдельный venv/lockfile/launcher, preflight и независимый uninstall |

**Зависимости:** установленный Node 24/CorelDRAW 27/VBA/PowerShell 5.1; Python 3.12 и Credential Manager; донорские MIT исходники; native-tools модель (для live agent, не для tests); доступное пользовательское хранилище; тестовый Chromium. Административные права, Docker, облачная БД, Redis, брокер сообщений и отдельный MCP HTTP transport не требуются.

**Ориентиры выпуска:** календарные обещания и оценки трудозатрат не фиксируются. Вехи определяются проверяемым результатом и зависимостями, а не числом дней:

1. M0: T0 + T1, transport/lifecycle и durable state доказаны; blocker решён до UI-polish.
2. M1: T2 + T3, read-only вертикальный срез и серверная политика готовы.
3. M2: T4 + T5 + T6 + T7, полный согласованный MVP проходит fake-provider/browser/safety tests.
4. M3: T8, временный live smoke, reviewed verification/cleanup, затем отдельное включение launcher оператором.
5. I1/I2/I3: только после M3 и отдельного выбора следующего набора функций; LAN/multi-user не добавлять попутно.

Критический путь: owned stdio -> durable idempotency/recovery -> Corel jobs/policy -> background agent -> API/UI -> live acceptance. После стабилизации DTO задачи UI, provider и access допускают независимую работу; изменения общего runtime/process ownership интегрируются последовательно.

**Откат:** остановить новые шаги и дождаться начатого Job; отключить/удалить web launcher, сохранив историю и отчёты. Продолжить существующую работу через CorelDRAW/Codex без изменения MCP. Старую версию host не открывать на несовместимой схеме БД; backup/migration проверяются до обновления.

## 15. Финальная проверка плана

- Согласованные пользовательские решения отражены в разделах 1, 7 и background tests, включая отличия от донора.
- Цель, границы, компоненты, идентификаторы, data flow, API, security, failure modes, migration/rollback и проверка определены.
- Native read-only/COM/cleanup ограничения имеют владельцев задач и отдельные acceptance tests.
- До implementation нет нерешённых пользовательских развилок. Port, schema/version, quotas и расположение файлов являются явно указанными проектными defaults, а не утверждением о существующем коде.
- Установленная SDK implementation исследована для lifecycle риска; выпуск требует выполнения T0, а не доверия к плану.
- Исходники, конфигурация, пользовательские документы и зависимости не изменены. Единственный созданный файл при планировании: этот план.
