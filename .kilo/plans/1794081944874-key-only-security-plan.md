# Упрощение разрешений: только защита ключей

> **Для исполнителя:** использовать `executing-plans` или `subagent-driven-development`; выполнять задачи последовательно через тесты RED/GREEN. Этот документ разрешает планирование, но не означает разрешение на commit, установку или изменение пользовательских данных.

**Цель:** удалить пользовательские разрешения и подтверждения из панели и MCP, сохранив защиту API-ключей и техническую корректность исполнения.

**Архитектура:** UI отправляет обычные команды без `read_only`, consent и approval. Host валидирует команды, фиксирует идентичность операции и сразу выполняет её; сохранение/экспорт заменяют существующий файл. Старая история доступна только для чтения и никогда не становится источником автоматического исполнения.

**Стек:** Windows, CorelDRAW 27, Node.js 24, TypeScript, PowerShell 5.1 STA, Python 3.12, FastAPI/Pydantic, React 19, Playwright, pytest. Новые зависимости не нужны.

**Спецификация:** согласованные решения находятся ниже в этом документе; отдельные файлы дизайна не создавать.

## Согласованные решения

- Удалить «Только чтение» из UI, HTTP-команд, snapshot/context и исполнительной политики.
- Удалить подтверждения VBA, закрытия документа и перезаписи. Не заменять их автоматическим нажатием «Разрешить» или скрытым approval.
- Существующий файл назначения Save As/export заменяется автоматически. Для `corel_save_document` и `corel_export` убрать `overwrite` из публичной MCP-схемы; внутренний режим замены всегда включён.
- Удалить `context_approved`, `vision`, `send_preview` из текущего профиля. Доступное проверенное PNG-превью передаётся модели автоматически, без согласий и capability-checkbox. Используемая модель должна поддерживать изображения и native tools.
- Удалить обязательную регистрацию CDR, ограничения по разрешённым output roots и `disabled_tools`. Прямые абсолютные Windows-пути допустимы без списка разрешений.
- Каталог результатов остаётся настройкой назначения, не разрешением. Новый текущий контракт: `Workspace.output_directory: str | None`, `RunCommand.output_directory: str | None`; явно переданное значение фиксируется для Run, иначе используется настройка workspace. `OperationContext.output_directory` хранит эффективное значение.
- Удалить обязательное ручное рассмотрение для допуска нового контекста после сбоя. Готовность определяется сервером по фактическим операциям, процессу-владельцу и свежему списку документов.

## Что сохраняется

- Credential Manager namespace `coreldraw_agent`, `SecretStr`, неизменяемая `credential_revision` текущего Run, отсутствие env/file fallback при недоступном Vault.
- Маскирование ключей в сообщениях, ответах, исключениях и журналах; запрет передачи известного ключа в аргументы MCP/VBA. Ключ отправляется только выбранному provider в Authorization.
- HTTPS для внешнего provider, запрет credentials в URL, redirect/proxy/fallback. Bootstrap, session cookie, CSRF, Origin/Host и локальная граница API сохраняются: они защищают настройку и использование ключей, не требуют пользовательских разрешений.
- Проверка JSON/schema, формата/расширения файла, существования родительского каталога, доступности файла, пределов размеров. Никакого shell/Invoke-Expression с пользовательскими аргументами.
- Сериализация COM, именованный mutex, точные `document_id`/`StaticID`, `request_id`, durable запись до dispatch, бюджеты с postchecks, Stop, unknown-outcome handling, проверка владельца процесса и cleanup.
- Защита внутренних history/assets/reports/marker путей и целостности download-артефактов. Удаляется разрешительный root для пользовательских файлов, не контроль того, какие байты сервер действительно публикует.
- `discard_changes` сохраняется как параметр семантики закрытия, а не approval: по умолчанию несохранённый документ не закрывается; явный `discard_changes=true` выполняется без диалога согласия.
- `readOnlyHint`/`destructiveHint` MCP остаются правдивыми метаданными. Они не разрешения этого сервера; внешний MCP-клиент может иметь собственные подтверждения.
- `prune_token` остаётся сверкой конкретного снимка удаляемых артефактов, не пользовательским разрешением. Автоматическую очистку не добавлять.
- Состав поддерживаемых функций/форматов панели не расширять сверх существующего адаптера. Убрать безусловное скрытие уже поддерживаемого `corel_close_document`; новые tools/форматы и отдельный файловый менеджер вне задачи.

**Ограничение защиты ключей:** произвольный VBA имеет права пользователя ОС и не является sandbox. Маскирование и разделение каналов не защищают Credential Manager от намеренно написанного VBA, самостоятельно обращающегося к ОС. Не обещать такую изоляцию.

## Исходное состояние и риски

- Чужие незакоммиченные изменения уже есть в `docs/agent-panel.md`, `webhost/corel_agent/__main__.py`, `webhost/tests/test_scripts.py`, а также новые restart-файлы. Перед реализацией перечитать `git status`/`git diff`; не откатывать и не перезаписывать их. При прямом конфликте остановить затронутую задачу.
- Главный риск: старый Run в `awaiting_confirmation` после обновления не должен внезапно выполнить VBA или заменить файл.
- Второй риск: удаление `read_only` не должно превратить recovery/storage ограничения в обходимые проверки. Классифицировать по фактическому tool/kind, а не по флагу клиента.
- Все проверки реализации выполнять сначала на fixture и копиях данных. Live остаётся opt-in, только временные документы и файлы; пользовательские рисунки и сторонние GMS не трогать.

## Фокус проверки

- Старый pending approval при reopen: ни одного нового dispatch, Run становится interrupted (задача 3).
- Ротация ключа или отказ Vault: старый Run использует frozen revision, ключ не попадает в JSON/логи и нет fallback (задача 1).
- Файл появился во время export или изменился download: ожидаемая замена через staging, но никаких неверифицированных download-байтов (задача 2).
- Stop и два одинаковых request_id одновременно: максимум один dispatch, после Stop следующие эффекты не отправляются (задача 3).
- Marker/barrier сменился во время recovery: новая блокировка не снимается, неизвестный исход не объявляется успешным (задача 4).

## Задача 1. Профиль и provider без согласий

**Файлы:** `webhost/corel_agent/contracts.py`, `settings.py`, `provider.py`, `runtime.py`, `app.py`; `webui/src/components/SettingsDialog.tsx`, профильные части `App.tsx`; `webhost/tests/test_settings.py`, `test_provider.py`, `test_privacy.py`, `test_api.py`, `test_runtime.py`; `webui/tests/settings.spec.ts`.

- [ ] Сначала добавить RED-тесты: минимальный профиль без трёх флагов сохраняется и запускается; существующий профиль с ними читается без повторного ввода ключа; любой доступный проверенный PNG попадает в следующий model request без consent; технически несовместимый provider даёт явную ошибку без повторов/fallback.
- [ ] Удалить три поля из текущего `ProfileSnapshot`; `Settings.freeze(profile_id)` больше не принимает `require_consent`. Вызов profile-test обновить согласованно.
- [ ] В `_read()` разрешить только конкретный старый формат `profiles.json` schema 1: удалить три известные legacy-поля при чтении, затем строго валидировать остальное. Новый save пишет schema 2. Перед первой заменой schema 1 сохранить одноразовую копию метаданных; неизвестные версии/поля и повреждение не игнорировать. `credential_revision` и Vault entries не менять при миграции.
- [ ] Удалить обе consent-проверки provider/runtime. Добавлять изображения после успешного результата/postchecks при наличии проверенного PNG; не создавать дополнительные COM-вызовы только ради этой настройки. При Stop/ошибке не продолжать model loop.
- [ ] Сохранить inline PNG validation и запрет загрузки внешних image URL. Base64 не записывать в текст истории/technical log.
- [ ] Упростить SettingsDialog до модели, API URL и API key. `saveProfile(model, baseUrl, key)` без дополнительных boolean. Показывать постоянный текст о передаче контекста и превью, не новый checkbox/approval.
- [ ] Дополнить `/profiles/{id}/test` синтетическим проверенным PNG вместе с native tool probe, без пользовательских документов, чтобы тест требовал и изображения, и native tools. Не угадывать capability и не делать скрытый текстовый retry.
- [ ] GREEN: сохранены тесты Vault unavailable, отсутствие ключа в профиле/HTTP/истории, ротация ключа во время Run, неправильный URL, удаление ключа из поля диалога при закрытии. Новые HTTP-запросы с удалёнными profile-полями получают 422, не становятся скрытой совместимостью.

## Задача 2. Прямые пути, автоматическая замена и весь MCP без denylist

**Файлы:** `src/config.ts`, `index.ts`, `server.ts`, `tools.ts`, `types.ts`, `jobs.ts`, `artifacts.ts`; `powershell/CorelAutomation.psm1`; файловые DTO и маршруты в `webhost/corel_agent/contracts.py`, `policy.py`, `runtime.py`, `app.py`, `assets.py`, `privacy.py`; `webui/src/components/FileActions.tsx`, файловые части `App.tsx`; `test/config-tools.test.mjs`, `test/fixture-server.mjs`, `test/helpers.mjs`, schema/export tests; `webhost/tests/test_policy.py`, `test_save_export.py`, `test_assets.py`, `test_manual_actions.py`; `webui/tests/files.spec.ts`.

- [ ] RED: открытие незарегистрированного CDR по абсолютному пути, export вне каталога workspace, Save As/export поверх sentinel без `overwrite`, неизменённый полный список MCP-tools при валидном или повреждённом старом config.
- [ ] Удалить `ToolPolicy`, чтение `CORELDRAW_MCP_CONFIG_ROOT`, регистрацию/dispatch через `isEnabled`, генерацию новых `tool_policy` и `writeToolPolicy`; удалить неиспользуемый `src/config.ts`. `createServer(jobs)` не принимает policy. Старый `config.json` не читать, не удалять и не переписывать. Исторические job reports с `tool_policy` не портить при чтении/компактизации.
- [ ] Удалить `overwrite` только из схем Save/export; строгие новые вызовы с ним отклонять. В `Save-Document` установить `StructSaveAsOptions.Overwrite = true`; убрать FILE_EXISTS, зависящий от разрешения. Сохранить отказ `DOCUMENT_ALREADY_OPEN` для назначения, открытого в другом документе.
- [ ] Экспорт сохранить через staging и атомарную публикацию; появление назначения до commit не требует нового согласия и приводит к замене. Preview продолжает писать свой уникальный внутренний файл. `overwrite` диагностического экспорта не менять: это другая функция, не диалог разрешений панели.
- [ ] Удалить `FileRef`, `PublicFileRef`, `FileRegister`, `output_root_id`, ограничения `@output/`, регистрацию `/files/register` и root-membership проверки. Workspace заменить согласованным `output_directory`; сохранить выбранный каталог как удобство, не prerequisite для абсолютного path.
- [ ] При чтении старого workspace с единственным output root перенести его path в `output_directory`; при нескольких roots не выбирать произвольный каталог, вернуть None и дать выбрать назначение. Старый context.output_root переводить в output_directory по его собственному path, старый output_root_id разрешать только по сохранённому workspace при чтении истории. Новые запросы со старыми root-полями отклонять. Добавить reopen-тесты для одного/нескольких roots и отсутствующего старого ID.
- [ ] Model-facing `path` совпадает с форматом существующей MCP absolutePath; общий Python-парсер проверяет абсолютность Windows, NUL, тип/доступность файла и расширение, но не принадлежность разрешённому каталогу. Не сохранять отдельный security-запрет обычных пользовательских junction/symlink. Внутренние store/assets/marker guards оставить.
- [ ] При ручном Save без path использовать текущий путь точного документа; для несохранённого документа вернуть `SAVE_PATH_REQUIRED`, если назначение не задано. Относительное имя из формы соединять с выбранным каталогом без VBA-переписывания; у модели path абсолютный.
- [ ] Удалить маскирование обычных путей из `Privacy.for_model`, сохранив маскирование секретов. `validate_vba` больше не блокирует path aliases как разрешительную политику; aliases не раскрывать автоматически. Known credential в VBA/пути/аргументах по-прежнему запрещён.
- [ ] `Assets.register_export(path, format)` не принимает root. Download проверяет зарегистрированные path, digest, тип и размер, но не root. Старую metadata.root можно игнорировать только при чтении уже зарегистрированного исторического артефакта; не принимать произвольный путь в download API.
- [ ] FileActions: «Открыть CDR» напрямую, «Каталог результатов» как настройка; нет «Разрешить каталог», «Зарегистрировать» или overwrite-checkbox. Кнопки зависят только от busy, цели и наличия назначения.
- [ ] GREEN: реальные fixture/file assertions, Unicode/пробел/апостроф в пути, назначение появилось во время export, сломанный фильтр оставил старый файл, недоступный родитель, неверное расширение, изменившийся download. Corel 27 Overwrite/SaveAs проверены через Context7; ссылку и ограничения записать в актуальную справку.

## Задача 3. Прямое исполнение без read-only и approval, с чтением старой истории

**Файлы:** `webhost/corel_agent/contracts.py`, `runtime.py`, `policy.py`, `store.py`, `app.py`, `observability.py`; новый узкий `webhost/corel_agent/persisted.py` только для legacy-декодирования; `webui/src/App.tsx`, `components/DocumentPanel.tsx`, `components/ApprovalDialog.tsx`, `useRunStream.ts`, `api.ts`; `webhost/tests/conftest.py`, `test_approvals.py`, `test_runtime.py`, `test_store.py`, `test_access.py`, `test_contracts.py`; `webui/tests/permissions.spec.ts`, `approvals.spec.ts`, `chat.spec.ts`, `scope.spec.ts`.

- [ ] RED: VBA и Close выполняются без confirm; manual/agent mutation без `read_only`; повтор команды с тем же ID не даёт второй dispatch; изменённые параметры/цель до dispatch всё ещё блокируются; старая ожидающая approval операция после reopen не исполняется.
- [ ] Удалить `read_only` из текущих `RunCommand`, `OperationContext`, `RunSnapshot`; убрать `pending_confirmation`, ConfirmCommand/ConfirmationRequest и активный confirmation event из нового runtime. `RiskDecision` оставить только `allow | deny` для валидности операции; `POLICY_VERSION` и default новых PreparedOperation поднять до 2. Старые PreparedOperation не переклассифицировать для исполнения, только читать как evidence.
- [ ] Удалить `_approval_waiters`, `confirm/_confirm`, ожидание approval из `_execute`, `/runs/{id}/confirm` и новые записи в approvals. Убрать read-only фильтр catalog/classify и безусловное скрытие Close. `new_document` остаётся выбором цели: отдельный Run без привязанного CDR, не разрешением записи.
- [ ] `OperationContext.allow_new_document` заменить на `new_document: bool = False`; manual Create/Open задаёт его автоматически, agent использует выбор цели RunCommand. В legacy context переименовать только известное allow_new_document, не ослабляя проверку точной привязки.
- [ ] Оставить повторную проверку operation/scope digest и точного документа непосредственно перед dispatch. Снять content-fingerprint барьер для Save/export: содержимое назначения больше не предмет согласия, а замена ожидается. Идентичность открытого CDR и целостность артефакта остаются проверяемыми.
- [ ] В Store/runtime recovery и storage admission заменить `not command.read_only` на классификацию фактической команды: agent всегда потенциально изменяющий; `corel_status`, inspect/preview и host diagnostics остаются наблюдениями. Это внутренняя классификация, а не клиентское разрешение.
- [ ] `persisted.py`: строгое декодирование только известных legacy полей snapshot/command/context/event. Использовать на `_snapshot`, `list_runs`, `get_command`, восстановлении context и чтении events. Не вводить глобальное `extra=ignore`; старые HTTP-аргументы read_only/consent не принимать.
- [ ] SQLite schema 1 и исходные command/event/report payload оставить для аудита; не DROP approvals/reviews и не переписывать старые fingerprints. При чтении убрать старые поля; старое confirmation-событие показать как informational warning с тем же seq/at/run_id, без диалога. Legacy `awaiting_confirmation` допустим только в историческом EventState, никогда не генерируется новым runtime.
- [ ] До публикации API `Store.recover()` завершает старый неотправленный pending Run как `interrupted`, инвалидирует approval и не вызывает MCP/model. Уже dispatching/known_job проходит прежнюю проверку исхода, не превращается в новую команду. Старые client_request_id не использовать для нового исполнения; конфликт версий payload остаётся REQUEST_ID_CONFLICT.
- [ ] Убрать approvals из новых metrics, сохранив старые данные в DB. UI: удалить readOnly-state/props, ApprovalDialog и живую обработку confirmation; история и поток не должны возвращать диалог. Предупреждение о немедленном исполнении оставить постоянным текстом.
- [ ] GREEN: Stop до dispatch/при принятии/during COM, budget reservation, SSE reconnect/пагинация без пропусков seq, старые completed/rejected/uncertain Run и reports сохраняют фактические outcomes. Удалённые endpoints дают 404, удалённые поля новых запросов 422.

## Задача 4. Готовность после сбоя без ручного разрешения

**Файлы:** `webhost/corel_agent/recovery.py`, `store.py`, `runtime.py`, `app.py`, recovery DTO в `contracts.py`; необходимые интеграции в `__main__.py` и существующем `restart.py`; `webui/src/components/RecoveryPanel.tsx`; `webhost/tests/test_recovery.py`, `test_recovery_api.py`, `test_reconcile.py`, `test_owner_guard.py`, `test_restart.py`; `webui/tests/recovery.spec.ts`, `reviewBarrier.spec.ts`, `safetyState.spec.ts`.

- [ ] RED: новый контекст доступен без review-checkbox после доказанного завершения; pending Job, unknown outcome, incomplete cleanup или непроверенный PID не допускают mutation; смена marker/barrier во время проверки не снимает новую блокировку.
- [ ] Удалить POST ручного допуска `/recovery/review`, release_for_new_context/manual_corel_checked и обязательный note. Старые reviews оставить читаемыми как аудит, без исполнительного действия.
- [ ] Добавить `async Runtime._refresh_readiness_locked()` для вызова только при уже захваченной `runtime._lock`: проверить неизменяемый ID барьера, отсутствие активных задач/незавершённых эффектов, пустой recovery_pending, текущего проверенного MCP-owner и свежий documents cache. Вызывать перед приёмом нового изменяющего Run и после завершения наблюдательных refresh/reconcile задач. Не захватывать ту же asyncio-lock повторно; до окончания refresh-task допуск отложить, а не обходить проверку активных задач.
- [ ] Допуск возможен только если связанные ранее отправленные операции имеют подтверждённый terminal result `succeeded|failed` и `cleanup.completed=true`, либо ledger доказывает, что операция вообще не была отправлена. При изменении состояния повторно проверить; снятие текущего глобального барьера атомарно по его ID. Старый snapshot/outcome не менять.
- [ ] `unknown`, dispatch без доказанного terminal, `cleanup.completed=false`/отсутствует и неясный owner остаются технической неисправностью, не снимаемой boolean пользователя. Автоматический ремонт произвольного VBA и доказательство неизвестного эффекта вне плана; показывать конкретную причину недоступности.
- [ ] `/recovery/reconcile` оставить действием сверки фиксированного reports root, без запуска model/MCP и без replay. Штатный restart остаётся процессным действием; новый MCP появляется только после подтверждённого завершения старого владельца. Не расширять принудительное завершение процессов.
- [ ] RecoveryPanel показывает причину и действия сверки/обновления, без «Разрешить новый контекст», checkbox и обязательного комментария. После серверной готовности новая переписка запускается обычной кнопкой.
- [ ] Обновить все прямые чтения legacy RunSnapshot в restart-checks через декодер задачи 3; не заменять и не терять уже существующую незакоммиченную реализацию restart.
- [ ] GREEN: совпавший/сменившийся barrier ID, известный terminal/unknown, пустой и неотправленный Run, live/reused/unknown PID, stale documents, failed cleanup; старый агентный план не возобновляется.

## Задача 5. Контракты, справка и итоговая проверка

**Файлы:** `webhost/openapi.json`, `webui/src/api.generated.d.ts`; `README.md`, `docs/architecture.md`, `docs/launch-and-use.md`, `docs/agent-panel.md`, `docs/agent-panel-api.md`, `docs/quick-start.md`; `skills/coreldraw-automation/SKILL.md`, актуальные `references/tools.md`, `references/api-v27.md`; fixtures/live scripts в `test/`, `webhost/tests/`, `webui/tests/fullstack.spec.ts`.

- [ ] Во время задач 1-4 регенерировать OpenAPI/types по необходимости; в финале выполнить `.venv/Scripts/python.exe -m corel_agent.export_openapi` из webhost и `npm run generate:api` из webui. Не править generated types вручную.
- [ ] Синхронизировать MCP descriptions/default metadata и руководство: никаких обещаний read-only/approval/disabled_tools; автоматическая замена и передача изображений указаны явно. Удалить устаревшие шаги разрешения/регистрации из действующих инструкций, но не переписывать исторические планы и verification reports.
- [ ] Источник Corel 27: `https://community.coreldraw.com/sdk/api/draw/27/p/structsaveasoptions.overwrite` и `/27/m/document.saveas`; Overwrite=true допускает замену, false приводит к ошибке. Live atomicity SaveAs не обещать; staging экспорта оставить.
- [ ] Обновить fixture-server, helpers, Python `command()`, браузерный fixture host и smoke/live scripts под новые схемы. Старые тесты approvals/read_only заменить проверками нового поведения, не просто удалить. Тесты API/ключей/владения/Stop/cleanup сохранить.
- [ ] Проверить отдельно, что CLI/MCP и панель одинаково заменяют существующие Save/export файлы. Обновить `test/integration.mjs` и relevant PowerShell export fixtures: больше нет ожидания FILE_EXISTS для этих инструментов.
- [ ] Добавить fixture fullstack сценарий: профиль без разрешений -> выбор документа -> VBA -> автоматические postchecks/PNG в model request -> export поверх файла -> история; ни одного confirm/register endpoint.
- [ ] Выполнить штатный gate из корня: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify-CorelAgent.ps1`. Он включает `npm test`, `npm run check:contracts`, pytest `-m 'not live'`, ruff, OpenAPI/type check, UI build и Playwright. Все провалы перечислить; новые зависимости/обходы gate не добавлять.
- [ ] Отдельно запустить migration/reopen tests на копиях schema-1 profiles/history с pending approvals и старым config. Убедиться в нулевом числе автоматически исполненных прежних операций и отсутствии secrets в backups/diagnostics/model body.
- [ ] Live только отдельно и opt-in: `npm run test:integration` использует временные документы; panel live использует `CORELDRAW_AGENT_LIVE_FIXTURE` и только заранее созданные временные документы. Сохранить диагностику, закрыть только созданное тестом; удалить только временные диагностические `CorelMcp_*` GMS.
- [ ] Финальный `git diff --check` и независимый review изменённых границ: скрытые permission ветки, старые payload, key leakage, повтор исполнения, export staging и recovery. Не коммитить без отдельного запроса пользователя.

## Внедрение и откат

- Сначала gate и fixture/migration проверки, затем согласованное обновление MCP build, webhost и webui. Не смешивать старый frontend с новым backend.
- Перед обновлением штатно остановить собственный host, дождаться drain; создать согласованную backup history и копию profiles metadata. Установка и restart пользовательского экземпляра не выполняются как побочный эффект реализации.
- Если остаётся прежний неизвестный Job, обновление не разрешает его повтор: новый host стартует в режиме наблюдения/сверки.
- Старый config остаётся на диске, но не действует. Старые approval/review записи остаются данными, не разрешениями.
- Откат: остановить новый host, восстановить предыдущую согласованную версию приложения и metadata/history backup. Не накатывать старую DB поверх работающего процесса и не переносить новые неоднозначные команды для replay. Vault revisions не удалять и ключи не копировать в файлы.

## Критерии готовности

- Нет пользовательских permission-checkbox/dialog и новых API approval/read_only/consent/file_ref/root-allowlist/disabled_tools.
- Действия выполняются сразу; замена Save/export автоматическая; доступное PNG передаётся модели всегда.
- Прямой путь не требует регистрации или разрешённого каталога; настройка назначения не ограничивает модель.
- Старые данные читаются без скрытого исполнения; неотправленный прежний approval становится interrupted.
- Новый контекст после доказанно устранённого сбоя не требует review; неизвестные/незавершённые операции не повторяются и не обходятся.
- Защита ключей, проверка схем, идентичность документов/операций, mutex, Stop и truthful outcomes сохранены; gate и целевые migration/fullstack тесты проходят.

**Открытых продуктовых вопросов нет.** Полный live gate и установка отдельным шагом; исправление неизвестных произвольных VBA-эффектов, расширение tools/форматов и отмена правил внешнего MCP-клиента вне задачи.
