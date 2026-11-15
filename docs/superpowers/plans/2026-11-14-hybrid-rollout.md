# План внедрения пакетного Hybrid

> **For agentic workers:** REQUIRED SUB-SKILL: Use `executing-plans` для последовательного исполнения в текущей сессии; шаги отмечаются `- [ ]`. `systematic-debugging` перед исправлениями, `test-driven-development` для RED/GREEN, `verification-before-completion` перед итогом. Новые Agent Manager sessions/worktrees, commit/push и установка требуют отдельного запроса.

**Goal:** закрыть обнаруженные дефекты приёмки и подготовить контролируемое использование hybrid для пакетных операций, сохранив C# default и отдельные gates установки/выпуска.

**Согласовано 15 ноября:** исполнять последовательно; текущий default сохраняется до приёмки и явного hybrid-пилота. После успешной приёмки подготовить отдельное решение о hybrid default с чистым C# для диагностики. Это не разрешение переключить default до получения результатов.

**Результат исполнения 15 ноября:** задачи выполнены до проверяемого ограниченного результата, отчёт `docs/hybrid-rollout-2026-11-15.md`, ledger `artifacts/hybrid-rollout/20261115/progress.md`. SourceOnly PASS, functional live218PASS, explicit hybrid pilotPASS, новые быстрые парыPASS. Tiles1000 C# остановлен даже на1802.6s, полная performance пара отсутствует; test-output lifecycle не запускался поверх shared runtime. Общий gate NOTPASS, default csharp неизменён, installer/release не выполнялись. Чек-листы ниже — исходные шаги плана; фактические выполненные команды/отклонения/исключения и результаты фиксированы в ledger, не предполагаются по пустому checkbox.

**Architecture:** использовать существующий C# STA worker с явным `CORELDRAW_MCP_EXECUTION_MODE=hybrid`, фиксированным GMS и текущей маршрутизацией поддерживаемых операций. Не вводить новый backend, автоматический выбор по числу фигур или fallback после отказа native. Сначала исправить shutdown и контракты, затем повторить измерения и провести opt-in пилот на собственных документах.

**Tech Stack:** Windows x64, CorelDRAW 27.2.0.135, Node.js 24, закреплённый .NET SDK 10.0.404, Python webhost, React UI, существующие pytest/xUnit/Node/Playwright suites.

**Spec:** согласованное направление из текущего диалога и фактический отчёт `docs/full-live-gate-2026-11-14.md`; ограничения допуска — `docs/test-machine-live-admission.md`; исходный gate — `docs/superpowers/plans/2026-11-14-full-live-gate.md`. Новый дизайн API/backend не требуется. План не запускает реализацию и не разрешает установку автоматически.

## Global Constraints

- Сохранить `csharp` default: `src/backend.ts:6`, один C# backend, без скрытого изменения пользовательского env/launcher.
- Все COM-вызовы и cleanup на одном STA под `Local\CorelDrawMcp_v27`; Node mutex tests не запускать параллельно live COM.
- Новые тесты работают только с собственными временными документами, exact document IDs/StaticID, без Application.Quit, автоматического Undo, replay или kill unknown исполнителя.
- Увеличенный performance budget — кооперативная граница между terminal командами, не отмена COM и не увеличение общего tool deadline; слот освобождается только после результата/cleanup.
- Live receipt принимает только зафиксированные старые неопределённости; строгий installer pre-upgrade остаётся отдельной обязательной проверкой, без применения receipt как override.
- Исходные legacy reports/SQLite/Run outcomes не переписываются. Новые unknown, partial вне fault-injection и cleanup failures останавливают пилот.
- Общий GMS не выгружать как временный. Test-output и worker-output пути различаются, их lifecycle проверять последовательно с exact path/hash/name/dirty.
- UI не переделывать; credentials/CSRF/bootstrap/owner-identity safeguards не ослаблять.
- Выпуск готового пакета блокируется неподтверждёнными правами на generated interop: `worker/INTEROP-DISTRIBUTION.md`. Разрешение тестировать компьютер не является разрешением Corel на распространение.
- Рабочее дерево уже содержит изменения предыдущего gate на ветке `14-11-2026`, HEAD `1cceb0b367bbf2e310c31a08ee32b954bfcfaa36`; их не откатывать и не принимать за новые изменения этого плана.

## Review Focus

1. Stop при открытом HTTP/SSE и выполняющейся мутации: завершить сетевые подписки, но сохранить drain MCP/COM и маркеры владельца до подтверждённого завершения. Тесты задачи 1.
2. OpenAPI/types расходятся вследствие генератора или серверной модели: обе генерации воспроизводимы, private data и произвольные overrides не появляются. Проверки задачи 2.
3. Budget истёк в последней операции/сохранении: не записывать ложный полный sample; учитывать завершившийся эффект без retry и без разрушения cleanup. Тесты задачи 3.
4. Неполные пары/дубликаты samples/warmup выданы за performance PASS: валидировать фактические rows по case/mode/iteration, не доверять одному `full_pairs_completed`. Тесты задачи 3.
5. Native module missing/path mismatch либо новый unknown: fail closed без fallback/повторного dispatch; C# default и старые исторические outcomes неизменны. Проверки задач 4–5.

## Исходные Результаты

Функциональная база: 217 .NET cases; 5 MCP integration suites; hybrid smoke 100 native operations/1000 фигур; 24 text CDR/1440 фигур с actual Arial/12 pt и геометрической парностью 0.001 mm. Node 159 passed/2 opt-in skipped, UI 30 passed. Это результаты конкретных артефактов, не независимый аудит всех текущих изменений.

Остатки: fixed-port host `TEST_HOST_SHUTDOWN_TIMEOUT`; stale generated UI API; C# tiles1000 stopped на 302221.8 ms. Hybrid tiles1000 median 5740.4 ms, полной C# пары нет. PNG ≈ одинаков, inspection hybrid немного медленнее; их специально ускорять в этом плане не требуется.

## Задача 1: Устранить Browser Shutdown Timeout

**Files:** `webhost/corel_agent/__main__.py`, `webhost/tests/test_launcher_acceptance.py`; только по доказанной причине — `webhost/corel_agent/events.py`, `webhost/corel_agent/app.py`, `webhost/tests/test_events.py`. Не менять `runtime.aclose`/`owned_stdio` ради обхода drain.

**Interfaces:** сохранить `serve(directory, *, no_browser=False)`, `run_controls(server, events, access, log, *, no_browser=False)` и `cleanup_resources(...)`. Stop перестаёт принимать новые запросы, завершает только принадлежащие host HTTP-подписки; MCP завершает принятую команду и cleanup до освобождения своего marker.

- [ ] Запустить исходный RED из `webhost`: `.venv\Scripts\python.exe -m pytest tests/test_launcher_acceptance.py::test_fixed_port_launcher_serves_compiled_ui_and_real_browser_without_com -vv`; сохранить traceback, без bootstrap URL/cookie/token в публичном отчёте.
- [ ] Локализовать ожидание до отмены: сохранить sanitized task stacks/HTTP connection state, `server.should_exit`, счётчик подписок и наличие незавершённой MCP-команды. Проверить актуальную документацию установленных Uvicorn/Starlette/sse-starlette через Context7; не объявлять SSE причиной только по timeout. Диагностика не отменяет native task.
- [ ] Закрепить минимальную причину отдельным RED regression в `test_launcher_acceptance.py`: после закрытия browser и Stop host завершается до существующего предела 10 s, fixture MCP returncode=0, оба owned markers отсутствуют, никакого второго dispatch. Если дефект не воспроизводится, сохранить повторяемость и не вводить предположительный production fix.
- [ ] Добавить соседний тест Stop во время slow fixture MCP: до terminal cleanup host не завершён и marker остаётся; после разрешения fixture drain host штатно выходит. Для stream-причины также проверить idle stream без Run и reconnect/disconnect.
- [ ] Исправить только доказанную точку блокировки. Не поднимать timeout теста, не включать `force_exit`, kill, blanket task cancellation или `timeout_graceful_shutdown=0`, способный отменить native drain.
- [ ] GREEN: весь `tests/test_launcher_acceptance.py`, `tests/test_launcher.py`, существующие shutdown/owned_stdio tests; затем полный `.venv\Scripts\python.exe -m pytest -q -m 'not live'` из `webhost`, без исключения проблемного case. Fixed-port case не должен быть SKIP из-за занятого порта: сначала определить владельца, чужой сервис не завершать.

**Приёмка:** полный webhost nonlive suite PASS, browser case действительно executed и markers очищены только после fixture drain; минимум 3 последовательных успешных целевых повторения, retries pytest не включать.

## Задача 2: Согласовать OpenAPI И TS Types

**Files:** `webhost/corel_agent/export_openapi.py`, `webhost/openapi.json`, `webui/src/api.generated.d.ts`, `webui/codegen/package.json`, `scripts/Verify-CorelAgent.ps1`; production модели `webhost/corel_agent/contracts.py`/роуты `app.py` не менять без обнаруженного контрактного дефекта.

**Interfaces:** источник истины `create_app().openapi()` без startup/lifespan; JSON snapshot экспортируется `python -m corel_agent.export_openapi`, types — только закреплённым `openapi-typescript 7.13.0`. Не редактировать generated types вручную.

- [ ] Сохранить RED двух отдельных проверок: из `webhost` `.venv\Scripts\python.exe -m corel_agent.export_openapi --check`; из root `npm --prefix webui run check:api`. Записать какая граница устарела.
- [ ] Сгенерировать схему в отдельный диагностический файл через `schema()` и сравнить с snapshot, не запуская host. Проверить дельту обязательных/nullable полей, recovery endpoints, 39-tool catalog и отсутствие возврата legacy arbitrary VBA. Если изменяется реальный API, сначала добавить RED endpoint/serialization test и исправить модель; не легализовать ошибочную схему генерацией.
- [ ] Для корректной текущей схемы выполнить из `webhost` `.venv\Scripts\python.exe -m corel_agent.export_openapi`, затем из root `npm --prefix webui run generate:api`; изучить оба diff, обновлять только нужную сторону при отсутствии drift другой.
- [ ] GREEN обеих `--check`, `npm --prefix webui run build`, `npm --prefix webui test`, Ruff и полные endpoint tests; не оставлять TypeScript casts вместо контрактного исправления.
- [ ] Повторить генерацию и убедиться в byte-stable результатах. Существующий `Verify-CorelAgent.ps1` уже содержит обе проверки — не дублировать новый генератор и не обновлять dependencies без необходимости.

**Приёмка:** schema/types согласованы с работающим сервером, build и 30 UI tests (или актуальное большее число) без failures, повторная генерация не меняет файлы.

## Задача 3: Завершить Честный Tiles1000 Benchmark

**Files:** `scripts/bench-hybrid.mjs`, `scripts/benchmark-correctness.mjs`, `test/benchmark-correctness.test.mjs`; создать `test/benchmark-options.test.mjs` для CLI validation без COM. Отчёты только `artifacts/hybrid-benchmark/<new-id>/`.

**Interfaces:** сохранить `benchmark({cases,samples,budgetMs})`; default `budgetMs=300000`. Добавить строго целочисленный CLI `node scripts/bench-hybrid.mjs tiles1000 3 --budget-ms 1800000`, допустимый диапазон `1..1800000`, только одна опция, без NaN/Infinity/дробей/неизвестных аргументов. Экспортировать `parseBenchmarkOptions(argv: string[]) -> {cases:string[],samples:number,budgetMs:number}` для тестов. Существующие вызовы без опции сохраняют смысл. Точный max 1800000 — предлагаемый для этого стенда 30-минутный лимит **каждого сценария**, не worker deadline.

- [ ] RED option tests: default 300000; explicit 1800000 принят; 0/1800001/NaN/Infinity/fraction/duplicate/unknown option и пустой/duplicate case отклонены до `connect`/создания документов.
- [ ] RED acceptance tests: фактические rows требуют по каждому requested case ровно warmup+samples обоих mode, уникальные iteration, конечный положительный `total_ms`, correctness и cleanup связаны с конкретной fixture. Missing/duplicate row, false flags, stopped/error/closed=false не PASS даже при `full_pairs_completed=true`.
- [ ] Минимально реализовать parser и metadata `requested_cases`, `requested_samples`, `budget_ms` в новом benchmark report; записывать `document_id` в sample и cleanup row для проверки связки fixture. Усилить `requireCompleteBenchmark(report)` с этой metadata. Старые отчёты без metadata остаются историческими, не переписываются и не получают PASS новой проверки автоматически.
- [ ] Проверять исчерпание бюджета до каждой измеряемой команды и после завершения измеряемой последовательности, включая Save: поздно закончившаяся команда остаётся terminal, но sample классифицируется stopped, не full. Partial save/cleanup выполняются отдельно вне измеряемого бюджета. Для тестов моделировать часы/terminal command, не ждать 30 минут и не отменять реальный COM.
- [ ] GREEN `node --test test/benchmark-options.test.mjs test/benchmark-correctness.test.mjs`; не менять fixture/число команд между режимами относительно предыдущих измерений.
- [ ] Проверить fresh live receipt, отсутствие активного внешнего worker, исходные docs/app state, exact runtime identity. Запустить **новую общую парную серию** `tiles1000 3 --budget-ms 1800000` с warmup, чередованием mode и теми же artifact correctness checks. Старые hybrid samples не склеивать с новыми C# samples.
- [ ] Планировать до 4 часов худшего случая (8 сценариев × 30 минут плюс подготовка/проверки); запускать через tracked `background_process monitor`, без terminal timeout/kill worker. Реальное время может быть значительно меньше. Не запускать одновременно другие Corel/bench workloads.
- [ ] Проверить 1000 IDs/центров/цветов/углов, сохранённые CDR, source/application-state/cleanup, worker/GMS hashes и versions. Speedup считать по median трёх полных nonwarmup samples; phase timings и setup/postcheck scope указать явно.
- [ ] Если C# снова не завершится — сохранить stopped/partial CDR, закрыть только owned terminal fixture и записать performance NOT PASS, без очередного автоматического увеличения бюджета или повтора unknown. Функциональный hybrid PASS остаётся отдельным фактом.

**Приёмка:** либо полный проверенный paired result, либо явный ограниченный вывод без выдуманного speedup; запуск benchmark не имеет exit 0 для неполной серии.

## Задача 4: Opt-In Пилот Пакетного Режима

**Files:** `src/backend.ts`, `src/csharp-executor.ts`, существующая маршрутизация `worker/CorelMcp.Worker/WorkerSession.cs`, `test/backend.test.mjs`, `test/csharp-executor.test.mjs`, `test/integration-hybrid.mjs`, `skills/coreldraw-automation/references/tools.md`, `skills/coreldraw-automation/references/recipes.md`, `docs/test-machine-live-admission.md`. Не создавать параллельный dispatcher.

**Interfaces:** использовать уже существующие `CORELDRAW_MCP_EXECUTION_MODE=csharp|hybrid`, `corel_create_batch`/`corel_update_batch` и текущие проверенные native routes. PNG/inspection остаются на существующем маршруте; режим hybrid не означает исполнение каждого tool через VBA. Нет автоматического переключения по порогу размера и нового fallback API.

- [ ] Закрепить/дополнить backend tests: отсутствие env даёт csharp; explicit hybrid проходит в worker; invalid mode отклонён; отсутствующий/неверный native runtime fail closed до мутации, без повторного dispatch другим движком. Существующий GREEN не превращать искусственно в RED: новые тесты нужны только на отсутствующие утверждения.
- [ ] Составить точную таблицу существующей маршрутизации из code + свежих reports: batch create/update, supported geometry/style/move/duplicate против C#-only/read-only routes. Не обещать ускорение PNG/inspection или всех вариантов текста.
- [ ] Описать два отдельных dev launch profile примера с process-local env и exact worker path: default C# и явно выбранный hybrid. Не менять installed host, пользовательский MCP config, системный PATH или default. Для UI host использовать штатный launcher, без нового UI переключателя.
- [ ] Выполнить последовательный opt-in пилот на новых fixtures: прямоугольники/эллипсы/текст, create+update batch, duplicate/move/style и PNG/inspection; mixed mm/inch origin, missing last target/font и locked ancestor до мутации. Переиспользовать existing suites; один фиксированный native runtime, никакой генерации/Compile на запрос.
- [ ] Проверить actual font/12 pt/physical geometry на собственных CDR через `LiveBenchmarkTextTests`, а не сравнение glyph bounds с insertion point; цвета/StaticID/полные деревья и cleanup по всем целям. Исходные пользовательские документы неизменны.
- [ ] Наблюдаемость: сохранять execution engine, stage, versions/hash, requested/applied/confirmed IDs, terminal outcome/cleanup, не писать prompts/paths/VBA в technical.jsonl. Не придумывать новый logging framework.
- [ ] Выход из пилота — прекратить новые dispatch, дождаться terminal/cleanup и штатно закрыть **свой** MCP; следующий запуск явно csharp. Это переключение будущих запросов, не rollback уже сделанных изменений. При unknown сначала recovery, не kill/replay/GMS unload.

**Приёмка:** opt-in пакетные сценарии корректны; default regression PASS; в отчёте разрешены только подтверждённые routes. До общего gate — статус «экспериментальный/тестовый opt-in», не production ready.

## Задача 5: Общая Приёмка И Отдельное Решение О Поставке

**Files:** `docs/full-live-gate-2026-11-14.md`, этот план; существующие `scripts/Verify-CorelAgent.ps1`, `scripts/worker-package.mjs`, `test/worker-package.test.mjs`, `worker/INTEROP-DISTRIBUTION.md` — без ослабления package gates.

- [ ] Зафиксировать новую ревизию и diff, SDK/runtime/Corel versions, хеши worker/interop/GMS/manifest/BAS, receipt и исходные документы. Все предыдущие RED evidence сохранить; выводы привязать к новой сборке, не просто скопировать прошлые 217 PASS.
- [ ] Без LIVE выполнить `powershell -NoProfile -File scripts/Verify-CorelAgent.ps1 -SourceOnly -DotnetPath <absolute SDK 10.0.404 dotnet.exe>`. Включает полный webhost suite, schema/types checks, UI build/test и Node/.NET; заранее собрать UI из задачи 2, чтобы server acceptance не обслуживал stale dist. Очистить LIVE только process-local, затем восстановить env. Gate без исключения fixed-port case.
- [ ] Отдельно sequential protocol/mutex, C# LiveSmoke, 5 MCP integration suites, functional .NET/hybrid guard/fault tests и hybrid smoke. Не запускать вслепую исследовательские complex-fill/showcase tests. При существующем worker-output GMS сначала проверить exact identity; test-output runtime не выгружает чужой/общий проект ради удобства запуска.
- [ ] Приложить новый tiles1000 paired result и быстрые text/style/move/duplicate/geometry series; text20 hybrid median <2000 ms и не менее 5× относительно C# на том же scope. PNG/inspection не обязаны ускоряться, но correctness/cleanup обязательны. Performance и functional имеют независимые статусы.
- [ ] Сверить final docs count/active selection/units/optimization/events, workers/markers и сохранность исторических источников. Использовать свежие outputs, а не утверждение по одному process name.
- [ ] Независимое ревью всего diff, включая незакоммиченные исправления предыдущего gate. Использовать внутренний `task general`, если доступен; иначе явно записать отсутствие независимого review. Не создавать видимые Agent Manager sessions без запроса. Critical/Important исправляются с RED/GREEN до приёмки.
- [ ] Обновить единую матрицу: historical admission; explicit test-machine receipt; source nonlive; C# functional; native functional; paired performance; installer; redistribution. Нельзя назвать «полный PASS», если хотя бы требуемый functional/schema/shutdown/performance gate failed/skipped.
- [ ] Установка — отдельное разрешение и отдельный проверяемый сценарий backup/upgrade/uninstall/restore на разрешённом окружении; не выполнять её в этом плане автоматически. Strict pre-upgrade должен пройти **без** live receipt; текущий unresolved historical gate не обходить очисткой истории.
- [ ] Публикация — только после юридически достаточного подтверждения interop прав, self-contained package/notices verification и отдельного разрешения release. До этого локальные source/fixture тесты не считаются проверкой выпуска.

## Порядок И Результат

`Shutdown → API contract → управляемый benchmark budget → новый paired run → opt-in pilot → общая source/live приёмка → отдельные installation/release решения`.

Задачи 1–2 независимы по коду, но исполняются последовательно в текущей сессии; Corel workloads всегда строго последовательно. Если tiles1000 остаётся stopped, сохраняется функционально подтверждённый experimental opt-in и performance ограничение, а не объявляется внедрение полностью завершённым. C# остаётся текущим default до приёмки; после успешной приёмки и пилота подготовить предложение о hybrid default с отдельным чистым C# диагностическим запуском. Фактическая смена требует отдельного решения.

Коммиты не включены в исполняемые шаги: в этом репозитории они разрешены только отдельным запросом пользователя. План сохранён для просмотра; реализация по одному этому документу не начинается.
