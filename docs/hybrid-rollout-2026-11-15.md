# Итог внедрения hybrid: 15 ноября 2026

## Решение

**Общий gate NOT PASS.** Явный hybrid-пилот и функциональные проверки прошли; текущий default остаётся `csharp`. Hybrid пригоден для согласованных тестовых пакетных операций. Переход на hybrid default пока не выполнен и не рекомендуется объявлять принятым до отдельного решения по незавершённому performance gate и lifecycle-проверке.

План: `docs/superpowers/plans/2026-11-14-hybrid-rollout.md`. Подробный журнал и локальные доказательства: `artifacts/hybrid-rollout/20261115/`. Исходный HEAD `1cceb0b367bbf2e310c31a08ee32b954bfcfaa36`, ветка `14-11-2026`; изменений предыдущего gate не откатывали. Установку, commit, push, смену installed config/default не выполняли.

## Исправления И Приёмка

| Область | Результат |
| --- | --- |
| Browser shutdown | Исправлен Windows/Python 3.12 listener counter leak после ConnectionResetError: реальные HTTP connections/tasks дренируются, закрытие listener обязательно, MCP/lifespan cleanup не отменяется. Детерминированный RED→GREEN, три browser-повтора PASS |
| Stop + SSE + mutation | Новый fixture-тест удерживает typed mutation до release; оба owner marker остаются до terminal cleanup, один dispatch, затем штатный выход |
| OpenAPI/types | Семантического drift не было: CRLF checkout против LF генератора. `.gitattributes` фиксирует LF; обе строгие проверки, повторная генерация и UI build PASS |
| Live admission review | Две подтверждённые P2 устранены RED→GREEN: финальная проверка WAL/journal после owner scans; fatal UTF-8 decoder вместо замены malformed bytes. Исторические reports/SQLite/outcomes не переписывались |
| Benchmark | Явный целочисленный `--budget-ms` до 1800000, default 300000, проверка позднего завершения, actual case/mode/iteration/document/cleanup связей; неполные пары всегда exit 1 |
| SourceOnly Verify-CorelAgent | PASS: .NET 166 passed/54 opt-in skipped; Node 166 passed/1 protocol opt-in skipped; webhost 401 passed/1 live deselected; UI 30 passed; Ruff/contracts/OpenAPI/types/build PASS |
| Protocol | Отдельный реальный C# process test PASS; Windows mutex case выполнен в SourceOnly, не параллельно COM |
| .NET functional live | 218 passed/0 skipped в выбранном functional наборе, `trx/final-live-functional-green.trx` |
| MCP integration | Все 5 suites PASS; первый launcher run остановился перед drawing из-за отсутствующего explicit backend env, оставшиеся suites выполнены отдельно с csharp, не повторяя уже прошедший первый |
| Hybrid smoke | 100 native operations, 1000 проверенных плашек, CDR/PNG и cleanup PASS; `artifacts/integration-hybrid/d2ba58f0-599b-4141-9685-90e49fe49a62/` |
| Явный pilot | Rectangles/ellipses/text, create/update/style/duplicate/save/preview PASS; неверная последняя цель отклонена без изменения остальных; исходные документы/active/app state сохранены |

Live-тесты учитывают существующий исходный документ, а не требуют закрыть его ради пустого Corel. Старые count=2 предпосылки import-тестов заменены baseline+2. Test-only native factory явно использует проверенный worker-output runtime; production path/hash/name checks и запрет fallback неизменны. Геометрический test fixture также использует этот root. RED-прогоны сохранены, финальный functional повтор зелёный.

Исследовательские ComplexFill/Showcase исключены из functional набора. Sealed-module test-output load/dispose lifecycle **NOT RUN** в этом прогоне: уже загружен shared worker-output runtime, выгружать его ради несовпадающего test-output пути нельзя. Свежий exact inventory и shared runtime functional выполнены; исторический lifecycle PASS от 14 ноября не выдаётся за новый.

## Производительность

Новая `tiles1000` серия: `artifacts/hybrid-benchmark/cadf0de6-9fb5-42b8-bde9-e89da50ce668/`.

- C# даже с увеличенным бюджетом остановлен на прогреве через **1802604 ms** между terminal командами; partial CDR сохранён, fixture закрыта.
- Hybrid: прогрев 5620.9 ms; три измерения 5830.8 / 5509.5 / 5478.9 ms, median **5509.5 ms**.
- `completed=true`, `full_pairs_completed=false`, все 5 fixture закрыты. Честный exit 1, speedup1000 **не вычисляется**. Дальнейшего автоматического увеличения бюджета нет.

Новая быстрая серия: `artifacts/hybrid-benchmark/7c9a978c-014c-46d2-bd30-da9401c4fef3/`, 7 cases × 2 modes × (warmup+3 samples), все пары и cleanup завершены. Text20: C# median **13033.4 ms**, hybrid **1333.0 ms**, примерно **9.78×**, цель <2000 ms и ≥5× выполнена. Style/move/duplicate/geometry также проверены, PNG/inspection не обязаны ускоряться.

Actual Arial/12 pt, текст и physical bounds всех 8 свежих text20 CDR (160 фигур) проверены отдельно, `trx/quick-text-proof.trx` PASS. Исходные COM identities/name/path/dirty/unit и active window сохранены. Дополнительно заново проверены 24 прежних CDR/1440 фигур, но их timings остаются историческими.

## Ревью И Принятые Решения

Независимое read-only ревью охватило весь существовавший dirty diff, включая предыдущий admission/Export/StateScope/LiveSmoke. Обе подтверждённые P2 исправлены; единый SSE+mutation coverage gap закрыт. Последующие test-only baseline/native-root адаптации проверены RED→GREEN/full suite, без повторного независимого ревью.

Принятые решения: работа в текущем согласованном dirty checkout без новых worktrees/коммитов; ручной Windows ledger вместо Unix helper; узкий Windows/Python3.12 HTTP adapter (проверять при обновлении Uvicorn/Python); LF вместо изменения API; неизменность исходных документов вместо предположения пустого Corel; shared-runtime test factory без unload; свежие быстрые серии с 3 измерениями вместо 5 и без повторного text100. Последствия: нет нового isolated clean-branch audit, нет нового test-output lifecycle PASS, text100 speed остаётся историческим.

Отложенное minor: полный stubbed runner regression `late Save → stopped → partial Save → close` отсутствует; clock guard/acceptance проверены, реальный cooperative stop проверен. Это не оправдывает false PASS неполной серии.

## Остаточные Gates

1. Полной пары C#/hybrid `tiles1000` нет даже при 30-минутном лимите: принять ограничение отдельным решением либо отдельно спроектировать иной сопоставимый benchmark. Не повышать бюджет бесконечно и не менять работу C# только ради красивого speedup.
2. Новый sealed-module lifecycle требует согласованного стенда с подходящей исходной GMS identity; текущий shared runtime не выгружался.
3. Строгий historical/installer pre-upgrade остаётся BLOCKED по прежним unknown; live receipt — отдельное принятие исторической неопределённости, не доказательство applied=false.
4. Установка/upgrade/uninstall и redistribution не проверены; interop distribution rights остаются обязательным блокером выпуска.

Следующий продуктовый шаг после решения этих gates — отдельное предложение включить hybrid default для поддерживаемых routes с чистым C# диагностическим профилем, без fallback/replay после native failure. До этого — явный opt-in hybrid и текущий C# default.
