# Полный live gate: функциональный PASS, остаточные ограничения

**Актуальное продолжение 15 ноября:** `docs/hybrid-rollout-2026-11-15.md`. Shutdown и LF/generated types исправлены, штатный SourceOnly PASS, новый functional live 218 PASS и hybrid pilot PASS. C# tiles1000 не завершил даже прогрев за1802.6s, поэтому полная performance пара по-прежнему отсутствует; default не переключён. Ниже сохранена история предыдущих прогонов, не текущая матрица готовности.

## Итог продолжения

**Общий gate: NOT PASS.** Подтверждены функциональные worker/MCP/hybrid проверки; неполная парная performance-серия и два webhost/UI-contract дефекта перечислены ниже. Установка, смена default backend, commit и push не выполнялись; старые reports и история не изменены.

| Проверка | Свежий результат |
| --- | --- |
| Новый явный fixture live admission | PASS; strict pre-upgrade всё ещё BLOCKED |
| C# LiveSmoke | PASS с проверкой точной COM identity активного owned документа |
| MCP integration | 5 suites PASS |
| .NET functional + fault injection | 217 passed, 0 skipped |
| Native lifecycle и exact GMS inventory | PASS, после worker-output runtime точный путь/dirty проверены без unload |
| Hybrid smoke | 100 native operations, 1000 фигур; CDR/PNG проверены, cleanup PASS |
| Empty/nonempty preview в обоих режимах | Empty → terminal COM_ERROR/applied=false, cleanup complete; nonempty PNG 4102 bytes + MCP image PASS |
| Финальный Node baseline | 159 passed, 2 skipped; protocol/mutex отдельно ранее PASS, параллельно COM не запускались |
| Финальный .NET без LIVE | 166 passed, 54 skipped; это пропуски opt-in live tests, не live PASS |
| TypeScript typecheck / Ruff / contracts | PASS |
| UI production build | PASS, исходный UI не переделывался |
| UI Playwright | 30 passed после актуализации legacy VBA ожидания на typed corel_edit_text; запрет corel_execute_vba проверяется |
| Webhost без fixed-port browser case | 396 passed, 1 skipped в двух последовательных запусках (389 + 7); не полный единый suite PASS |
| Fixed-port browser shutdown | FAIL: TEST_HOST_SHUTDOWN_TIMEOUT, UI assertions после rebuild проходят |
| UI generated API check | FAIL: api.generated.d.ts не соответствует существующему openapi.json; автоматически не перегенерировалось |

**Performance:** быстрая серия завершена полностью, 5 пар измерений + warmup для каждого из 8 cases. Независимое открытие собственных копий всех 24 text CDR подтвердило фактический Arial/12 pt и физическую эквивалентность 1440 текстовых фигур между режимами/повторами с допуском 0.001 mm. Точка вставки не приравнивается к glyph bounding box: первоначальная такая гипотеза опровергнута (bearing 0.3344 mm); проверяется actual геометрическая парность, а не ослабляется несовпадение режимов.

Tiles-серия `artifacts/hybrid-benchmark/ea184d2e-0b59-406e-a976-70314b4a1144`: tiles20 C# median 10270.9 ms / hybrid 797.0 ms (12.89×), tiles100 92879.0 / 1125.0 ms (82.56×), по 3 полные пары + warmup. C# tiles1000 остановлен **между завершёнными командами** на 302221.8 ms, частичный CDR сохранён, fixture закрыта. Hybrid tiles1000: 3 измерения, median 5740.4 ms, все postchecks прошли. Полной пары нет, поэтому speedup tiles1000 не вычисляется; CLI корректно вернул exit 1 (`full_pairs_completed=false`), а не ложный PASS. Все performance fixtures закрыты; budget не прерывал native mutation, worker не убивался.

**Конечные доказательства:** `artifacts/full-live-gate/20261114T175719Z/final-continuation-verification.json`, TRX, `performance-summary.json`, preview-fixtures/verification.json, два benchmark.json, 24 text-proof JSON. Полный review другим агентом не выполнялся: внутренний Task tool недоступен, новые видимые Agent Manager sessions без запроса не создавались; выполнен локальный review diff и проверяемые negative tests.

Дальнейшая работа — устранить fixed-port shutdown и устаревший generated UI API отдельными RED/GREEN, затем решить отдельно необходимость полного tiles1000 C# прогона вне текущего 300-секундного performance-контракта. Не называть принятые исторические unknown восстановленными.

## Обновление после отдельного разрешения пользователя

Пользователь разрешил изменение admission и мутации, указав, что компьютер тестовый. Добавлен отдельный явный `owned-fixture-live-tests` receipt: `docs/test-machine-live-admission.md`. Старые reports/SQLite/applied сохранены; строгий installer pre-upgrade по-прежнему BLOCKED. Первоначальная часть отчёта ниже сохранена как состояние до разрешения, а не актуальное утверждение, что live не выполнялся.

Свежие результаты продолжения: C# LiveSmoke PASS после диагностики активации окна; 5 MCP integration suites PASS; native repeated handshake/dispose lifecycle PASS; .NET functional повтор **217 passed, 0 skipped**; hybrid smoke **100 native operations / 1000 tiles**, CDR и PNG проверены, оба owned documents закрыты. Доказательства `trx/live-functional-fixed.trx`, `integration.log`, `artifacts/integration-hybrid/5eecb6f3-aea3-41c6-809a-315ca0a2d224/verification.json`.

Неживой baseline до последних исправлений: Node **158 passed, 1 skipped**, .NET **166 passed, 52 skipped**, contracts/typecheck/Ruff PASS. Финальная регрессия после всех исправлений ещё обязательна.

Внесённые исправления: собственный smoke активирует единственное окно точно owned документа с postcheck COM identity; export filters освобождаются перед атомарным File.Replace; StateScope захватывает исходное окно и после восстановления проверяет точную identity исходного active document. Существующий all-formats export тест сначала выявил PLT replace failure, затем active restoration failure; targeted повтор → 2 passed, полный functional повтор → 217 passed. Исходные failed TRX/reports сохранены.

Парная быстрая серия `artifacts/hybrid-benchmark/38696605-f15a-4655-a81c-39e7f7dbb6b2`: 5 измерений + прогрев, все 8 cases/пары завершены, stopped=[]/cleanup closed. Медианы: text20 C# 13018.7 ms / hybrid 1325.0 ms (9.83×), text100 64174.8/3195.7 ms (20.08×), style100 11172.6/1714.6 ms, move100 5416.7/1670.9 ms, duplicate100 5988.4/1824.5 ms, geometry100 28004.0/3746.8 ms, PNG 314.6/311.7 ms, inspection 1833.1/2042.0 ms. Проверка actual font/size/геометрии текстовых CDR проводится отдельно вне timing; до неё text correctness не считается полной. Tiles-серия ещё выполняется.

Webhost: первый запуск из root дал 3 failures (child import, stale 37-tool assertion, stale compiled UI). Штатный cwd устранил import issue; тест каталога обновлён до 39 с обязательными create/update batch и прошёл; UI пересобран без изменения исходного UI. Повтор всей webhost suite без launcher acceptance: **389 passed, 1 skipped**; отдельно остальные launcher acceptance **7 passed**. Fixed-port browser assertion после rebuild проходит UI-часть, но остаётся **TEST_HOST_SHUTDOWN_TIMEOUT**; это известный неустранённый failure, общий webhost PASS не заявляется.

Full live/performance gate пока не объявлен PASS. Установка, commit/push и смена default backend не выполнялись.

---

**Результат: BLOCKED, не PASS.** Исполнение плана согласовано пользователем на ветке `14-11-2026`, HEAD `1cceb0b367bbf2e310c31a08ee32b954bfcfaa36`. До исполнения tracked/staged diff пуст; единственный untracked-файл — исходный план. Ветка не переключалась, worktree/сессии не создавались.

Доказательства этой сессии: `artifacts/full-live-gate/20261114T175719Z/`. Каталог приватный, игнорируется Git; SQLite, логи и legacy BAS не предназначены для публикации.

## Что выполнено

### Стенд и резервная копия

- Windows `10.0.26100`, Node `24.18.1`, локальный SDK `10.0.404`, .NET/runtime `10.0.15`, CorelDRAW `27.2.0.135`.
- Системный SDK — `8.0.428`; PATH не менялся. Использован локальный SDK из прежнего worktree по пути, указанному в `docs/live-handoff-build-2026-11-14.md`.
- CorelDRAW PID `24980`, creation time `2026-11-14T17:48:12.6846570Z`, UI process identity `windows-proc-v1:13439152092:6846570`. Это не прежний PID `24044`. Эта сессия не запускала и не перезапускала CorelDRAW.
- Перед копированием SQLite проверено отсутствие WAL/SHM/rollback journal. Диагностическая копия создана SQLite backup API из immutable read-only connection, а не копированием главного файла при игнорировании WAL.
- `PRAGMA integrity_check` копии → `ok`; `foreign_key_check` → пусто. SHA-256 копии: `8dea9a38c24a7d1d2895d522fe33f75c9dc0ad7603ae3da9f196ada383a7948c`.
- Сохранены 83 исходных report, доступные run.log и 42 дополнительных job-артефакта. Три исторических GMS уже отсутствовали по указанным в reports путям: `CorelMcp_dc3c5d1173cb.gms`, `CorelMcp_f285b7059622.gms`, `CorelMcp_d1e9350e4420.gms`. Они не восстанавливались и не удалялись; отсутствие файла не доказывает отсутствие загруженного проекта или неприменение мутации.
- Все исходные reports, логи, основной SQLite и состав стандартных runs roots сохранили исходные хеши/инвентарь после диагностики. `source-manifest.json`, `job-artifact-manifest.json` и `postflight.json` содержат проверяемые результаты.
- Хеши worker EXE/DLL, interop, manifest, GMS, BAS и dist сохранены в `build-hashes.json` и `postflight.json`. Worker/interop/GMS не пересобирались.

### Владельцы и read-only Corel baseline

В host root нет `runtime.json` и `mcp-owner.json`; в SQLite нет unfinished Run, `recovery_pending=[]`, barrier отсутствует. Проверены все 24 PID владельцев, перечисленных в старых reports: Windows process API возвращает `winerror=87`. Старые creation_time не восстановлены; отсутствие по имени процесса не использовалось как единственное доказательство.

В отдельном C# MCP-сеансе выполнены только два `corel_status`, без hybrid readiness/загрузки GMS:

- jobs `3829bd7a-909b-46f7-8e7e-f3c14583650e` и `99c05697-ccc2-4ea8-b612-bb29ae8f4a15`;
- оба succeeded, cleanup completed;
- `documents=[]`, `active_document_id=null`;
- `application_state={optimization:false,events_enabled:true}` до и после;
- Corel `27.2.0.135`, worker/server `0.4.0`, protocol 1, x64, `.NET 10.0.15`, execution engine C#;
- MCP закрыт после terminal reports; после проверки `CorelMcp.Worker.exe` отсутствует, Corel PID/start identity прежние.

Доказательства: `readonly-baseline.json`, `baseline-verification.json`, `readonly-runs/*/report.json`. Read-only наблюдение не выдаётся за прохождение мутационных suites. Проверка точной loaded GMS identity не выполнялась, поскольку этап 2 остановил переход к этапу 3.

## Все обнаруженные исторические блокировки

В SQLite 25 Run, 43 операции, 9 terminal failed; все девять имеют `applied=null`, в error оригинального report нет `applied=false`. В CorelDrawMcp runs root 83 terminal reports, из которых 11 failed не удовлетворяют admission; два дополнительных VBA reports не найдены среди операций SQLite. CorelDrawAgent runs root отсутствует.

| Job ID | Tool | Ошибка | Связан с SQLite | Retained project |
| --- | --- | --- | --- | --- |
| `2f5cc0da-1309-484b-b7cb-831ffd5c3372` | corel_preview | STALE_DOCUMENT | да | — |
| `341d6180-f0a2-4750-a547-5101fc4dc314` | corel_preview | STALE_DOCUMENT | да | — |
| `4f1b9245-da51-4b2a-b352-79f720d0e4b5` | corel_inspect_document | STALE_DOCUMENT | да | — |
| `61621dc8-e65f-48d3-b0d6-20ad9e6dda88` | corel_inspect_document | VBA_UNAVAILABLE | да | — |
| `97539868-58b4-4293-aa0d-21312c766ecc` | corel_preview | STALE_DOCUMENT | да | — |
| `a05efa22-8e4b-4e5e-bdb8-02c2bb057043` | corel_preview | STALE_DOCUMENT | да | — |
| `f640be4a-0e57-423c-931c-20cc489e6e61` | corel_status | COM_ERROR | да | — |
| `req-2977c11ddedc3ef03ef71dfcc878752ed827f5d7361d387b7975ce207fcff0de` | corel_execute_vba | COMPILATION_FAILED | да | CorelMcp_dc3c5d1173cb |
| `req-3df54f39e56210fde15fd6d439a2368285752476dee88ceaf0bc36cf9a132e06` | corel_close_document | STALE_DOCUMENT | да | — |
| `req-b81baa12ad8efabc2458cce737b9acecf94f1a031b1b3b54ff372c6d2be223b1` | corel_execute_vba | COMPILATION_FAILED | нет | CorelMcp_f285b7059622 |
| `req-de25a439857c797008caf13a64ac30e5468fa56399ba783099521a7fac15e344` | corel_execute_vba | VBA_RUNTIME_ERROR | нет | CorelMcp_d1e9350e4420 |

Полная связка run/operation/job, stages, owner PID, applied/partial, cleanup и SHA-256 report: `history-inventory.json`, `report-inventory.json`. Подтверждённых `partial=true` в этих reports нет, но отсутствие applied не превращается в `false`. Для `CorelMcp_dc3c5d1173cb` существующий cleanup audit уже подтверждён verifier; mutation obligation всё равно не разрешён. Два других retained проекта не получили такого подтверждения в этом прогоне.

Все 43 ledger reports отличаются от disk reports только полями `log_tail`/`log_truncated`, добавленными в ledger. Это не обнаруженное противоречие исходов; см. `history-report-differences.json`. Оба представления и их commitments не подменяют друг друга.

## Точная причина остановки

`Store._readiness_proof` на копии возвращает `reason=null`; однако `webhost/corel_agent/preupgrade.py:63` дополнительно требует false в двух applied-полях. Отдельный `scripts/check-preupgrade.mjs:93` проверяет disk reports и также блокирует отсутствие applied. Исправление только SQLite либо очистка retained GMS проблему не решает.

`webhost/corel_agent/recovery.py:40` не создаёт отсутствующее доказательство. В текущем состоянии нет owner marker/pending binding для штатного reconcile; искусственно создавать их нельзя. POST recovery review отсутствует и не может использоваться как ручной override. Повторный restart/reconcile не обещает admission.

По стоп-условию этапа 2 дальнейшее исполнение остановлено. Подготовлен отдельный **несогласованный проект** `docs/superpowers/specs/2026-11-14-historical-admission-recovery-gap.md`: append-only resolution, привязка к неизменным evidence, независимые cleanup/mutation obligations и fail-closed verifier. Реализация требует отдельного согласования; проект не обещает разрешить все 11 случаев при отсутствии достаточных исторических доказательств.

## Выполненные команды и проверки

| Команда/сценарий | Exit | Подтверждённый результат |
| --- | --- | --- |
| `git status --short --branch`, `git rev-parse HEAD`, `git remote -v`, `git worktree list --porcelain`, просмотр diff/log | 0 | актуальные branch/HEAD/origin зафиксированы |
| `node scripts/check-preupgrade.mjs` | 1 | MUTATION_OUTCOME_UNRESOLVED, histories=1; fail-fast reports=0 не означает отсутствие reports |
| локальный `dotnet.exe --info` | 0 | SDK 10.0.404, runtime 10.0.15 |
| `webhost/.venv/Scripts/python.exe artifacts/full-live-gate/20261114T175719Z/audit.py` | 0 | копия SQLite, inventory всех стандартных reports, неизменность оригиналов |
| `.../python.exe .../compare-history.py` | 0 | 43 отличия только по двум log-полям |
| `node artifacts/full-live-gate/20261114T175719Z/baseline.mjs` | 0 | два succeeded status, 0 документов, совпадающий application_state |
| `.../python.exe -m pytest webhost/tests/test_preupgrade.py webhost/tests/test_recovery.py webhost/tests/test_recovery_api.py webhost/tests/test_retained_cleanup.py -q` | 0 | 53 passed; финальный повтор 52.37 s |
| `node node_modules/typescript/bin/tsc --noEmit -p tsconfig.json` | 0 | typecheck PASS |
| `.../python.exe -m ruff check webhost/corel_agent webhost/tests` | 0 | All checks passed |
| повторный admission | 1 | та же неразрешённая историческая мутация |
| `git diff --check` | 0 | нет tracked whitespace defects; новые документы дополнительно проверяются отдельно |

Сохранённые журналы команд: `postflight.json`, `recovery-tests.log`, `typecheck.log`, `webhost-lint.log`, `final-admission.log`. Production-код не изменён. Полный Node/.NET baseline, contracts, notices, protocol/mutex и мутационные suites не запускались: это следующие этапы плана, а не якобы пройденные проверки. Отдельного root lint script нет; webhost Ruff выполнен.

## Матрица gate

| Раздел | Статус |
| --- | --- |
| Baseline, доступная evidence backup, сохранность оригиналов | выполнено с явным перечнем трёх уже отсутствовавших GMS |
| Исторический admission | BLOCKED: 9 history / 11 disk failed reports без applied=false |
| Read-only C# status | PASS, только два status |
| Точная loaded GMS identity и новый Corel native lifecycle | не выполнялись |
| Неживой полный baseline этапа 4 | не выполнялся; точечные recovery tests/lint/typecheck выполнены отдельно |
| C# LiveSmoke / MCP integration | не выполнялись |
| Hybrid functional / fault injection / preview fixtures | не выполнялись |
| Performance / text font-size-geometry postchecks | не выполнялись |
| Full live gate | BLOCKED, не PASS |

Установка, смена default backend, перезапуск CorelDRAW, выгрузка GMS, изменения оригинальной истории, replay, commit и push не выполнялись.
