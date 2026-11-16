# Живой запуск 13 гибридных сценариев — 15 ноября 2026

## Дополнение: VBA размножение и сетка визиток

В коде `hybrid` подключён `corel_grid_duplicate` к существующему неизменённому fixed clone runtime, добавлены безопасные вложенные группы для duplicate/grid. При ≥20 новых objects (включая descendants) выбирается VBA; новые IDs ≤1000, текст ≤100000 UTF-16. Сложные заливки/обводки, bitmap и child-in-parent сохраняют C# preflight route. Default C# и установленный GMS не менялись; installation/upgrade gate не снят.

Расчёт сетки и полный независимый ID/структурный/геометрический/наблюдаемый appearance/text postcheck остаются C#. Проверки выполняются для каждого descendant; raw wire IDs остаются roots. Подтверждённые descendants сохраняются также при partial root без item. Baseline aliases, неверные affected claims, дополнительный незаявленный root, повреждения текста/оформления/геометрии вызывают unknown/barrier, без replay. Обход bounded baseline+planned descendants ≤51000. Полная equivalence rich-text runs/ICC/всех свойств Corel не заявляется.

Доказательства: 15/15 новых `LiveNativeDuplicationTests`, включая пять fault cases, каждый из которых до исправления возвращал ошибочный known partial вместо unknown; pure routing/row tests прошли. Общий functional .NET live: **236/236 PASS**, TRX `artifacts/native-grid-gate-20261115/native-grid-live.trx`, тот же ранее описанный фильтр lifecycle/showcase исключений. Публичный hybrid integration: **13/13 PASS**, `artifacts/integration-hybrid/6968ebd7-0db3-4b27-84fa-06430afb572b/verification.json`. Все fixture закрыты, исходные пользовательские документы сохранены. Повторное независимое статическое review закрыло обнаруженные findings, новых блокеров не нашло.

Финальный SourceOnly после этих правок: **PASS**, .NET170/64 opt-in skipped, Node169/1 skipped, webhost401/1 live deselected, UI30. Ruff, types/build, contracts и generated API check прошли. Финальные affected-alias/extra-root fault tests дополнительно сравнивают полный набор наблюдаемых новых IDs с retained IDs, не только корректность отдельных возвращённых значений: 5/5 PASS.

Парный single-command benchmark сгруппированной визитки (фон + две надписи), 85×50 мм, 96 экземпляров встык, 1020×400 мм: прогрев + три замера с чередованием режимов, подготовка и последующая инспекция вне замера, все pre/postchecks worker входят. **C#: 10.17 с; VBA: 7.58 с; ускорение1.34×**. Report и оба CDR: `artifacts/native-grid-benchmark/8575945e-5165-474b-8102-bd5f9130c442/`. Это не прежний 4.68× benchmark двух команд по негруппированным leaves.

Промежуточный benchmark `artifacts/native-grid-benchmark/1ddd68fe-f877-449c-8436-12fc9079cbd1/` дал C#10.27 с/VBA12.07 с: дублирующие COM-getters postcheck были устранены без ослабления проверок. Красные тесты и более медленный результат сохранены как evidence, не переписаны.

## Дополнение: расширенный .NET live повторно проверен

После результата 193/217 расширенный набор заново выполнен трижды: **217/217 passed, 0 failed, 0 skipped** в каждом запуске. Длительности 3:45, 3:46 и 3:46. Тот же фильтр исключает только `LiveComplexFillDiagnosticsTests`, `LiveHybridShowcaseTests`, `LiveBenchmarkTextTests`, `LiveHybridTests` и `LiveRuntimeInventoryTests`; bitmap/import, join, guards, fault-injection и новая page/preview регрессия не исключались.

TRX: `artifacts/extended-live-20261115/extended-live-diagnostic.trx`, `extended-live-repeat.trx`, `extended-live-without-journal.trx`. Каждый запуск предварялся успешной проверкой неизменного live receipt. Отдельный bitmap/import-тест также прошёл за 14 секунд. Перед прогонами нет чужих testhost/worker, mutex-тесты параллельно COM не запускались.

Для следующей диагностики в test-only DrawingClient добавлен opt-in `CORELDRAW_TEST_PROGRESS_JOURNAL=1`: `progress.jsonl` записывает принятую команду/job ID/стадии ещё до terminal результата. Это локальная диагностика fixture, не production technical log. Первые два новых прогона имели журнал; третий выполнен **без журнала** и тоже прошёл, поэтому синхронный I/O не выдаётся за исправление гонки. Production-код в этом дополнительном расследовании не менялся, таймауты не увеличивались, тесты не отключались и unknown-операции не переигрывались.

Текущий результат расширенного набора — PASS; причина прежних неповторившихся timeout **не доказана устранённой**. Предыдущие красные результаты и сверки ниже сохранены как история, а не переписаны зелёными исходами. Sealed-module lifecycle и остальные исключённые сценарии этим PASS не покрыты. Свежий SourceOnly снова прошёл: .NET 166/55 skipped, Node 169/1 skipped, webhost 401/1 deselected, UI 30; Ruff, types/build и контракты без ошибок.

## Допуск

Строгий `node scripts/check-preupgrade.mjs` по-прежнему возвращает `MUTATION_OUTCOME_UNRESOLVED`. Исторические SQLite/reports, applied и retained GMS не изменялись; установка и обновление не разрешены этим прогоном.

Для собственных временных документов использован существующий отдельный receipt `artifacts/full-live-gate/20261114T175719Z/live-acceptance.json`. Перед live-блоками выполнялась его штатная проверка `node scripts/check-live-admission.mjs --receipt <receipt>`. Снимок неизменён: `07afcf47473ffe2d62bc31611f088d369a694d10050049f6f41d1b82495fc90d`, 12 исторических исключений, 24 проверенных остановленных владельца. Один промежуточный owner scan отказал с `OWNER_NOT_STOPPED`; этот запуск тестов не начался, последующая новая проверка прошла. Новое принятие исторических исключений не создавалось.

Свежий read-only `LiveRuntimeInventoryTests` прошёл с exact worker-output GMS path. Shared runtime не выгружался. Среда: Node 24.18.1, локальный SDK 10.0.404/runtime 10.0.15, CorelDRAW 27.2.0.135. Backend `csharp`, execution mode явно `hybrid` только внутри тестового процесса; default и установленная конфигурация не менялись.

## Обнаруженные и исправленные ошибки

1. Добавленная страница наследует обычные слои документа: ожидание ровно одного слоя неверно. Сценарий импорта создаёт собственный слой и использует возвращённый opaque ID, проверяя его присутствие и editable-флаг.
2. Координаты неактивной страницы зависят от её активации. `DrawingTarget` фиксировал origin до переключения страницы; фигуры оказывались вне уменьшенной страницы, preview возвращал COM E_FAIL. Теперь точная страница активируется перед чтением origin, только если её TreeNode handle отличается от активной страницы документа. Все вызовы остаются под STA/mutex и StateScope; BeginCommandGroup по-прежнему открывается после полной проверки входов.

Регрессия `PreviewSecondResizedPageUsesItsBoundsAndPreservesAllDocuments` воспроизвела COM E_FAIL до исправления и прошла после него. Проверяет вторую страницу 120×90 мм документа 760×490 мм, PNG 600×450, синий пиксель в заранее заданной точке, неизменность source/control и исходного списка документов. Отдельно прошли эта регрессия, join-endpoints и import-preparation fault-тест: 3/3.

## Итог запрошенного запуска

Финальный `node test/integration-hybrid.mjs`: **PASS**, 13/13 сценариев, 32 функции, 7 успешных VBA-операций, две проверки перемещения, 1000 плашек с пагинацией и PNG, CDR save/reopen, PDF, preview и отрицательные случаи.

Доказательства: `artifacts/integration-hybrid/239bf400-6265-4bb2-86da-4326e3995070/verification.json`, CDR/PNG/PDF и отдельные reports в этом каталоге. Исходный и конечный список документов совпали, cleanup завершён. Прежние красные прогоны `4e9fcec6-a409-4331-9d55-7e2b417eb927` и `66c695f1-8ac0-4eaa-87f4-a263b5d873f4` сохранены. Зелёный промежуточный `d616db02-a204-4160-9652-922442f89767` не заменяет финальный прогон.

Финальный `Verify-CorelAgent.ps1 -SourceOnly`: **PASS** — .NET 166 passed/55 opt-in skipped, Node 169 passed/1 skipped, webhost 401 passed/1 live deselected, UI 30 passed. Ruff, TypeScript/UI build, contracts, OpenAPI и generated API check прошли.

## Ограничение расширенной проверки

Дополнительный общий .NET functional live-набор **не прошёл**: последовательные попытки дали 173/217, 191/217 и 193/217 passed. Первичные timeouts возникали в `JoinRequiresActualEndpointsWithinExplicitTolerance`, `ImportPreparationDocumentClosesAfterUndoStartsButDrawingBodyFails`, затем `ImportedBitmapsCanBuildMixedLayoutsWithoutWeakeningVectorOnlyOperations`; оставшиеся тесты получали BUSY после зависшего STA. Их не следует считать независимыми функциональными отказами или PASS. Точная причина нестабильности общего набора не установлена; успешные узкие проверки не доказывают её устранение.

После завершения каждого аварийного testhost выполнялась read-only сверка. Оставшаяся join-fixture имела исходный geometry revision и два subpath/четыре узла; операция не переигрывалась. Import/bitmap-fixture сверены с журналами и фактическими shapes. Только собственные документы сохранены в новые CDR и закрыты; до финального hybrid-прогона исходные ноль документов восстановлены. Диагностика: `artifacts/post-functional-status/`, включая readback и reconciliation JSON. Старые reports не переписывались, Corel не завершался, чужие GMS не трогались.

**Вывод первоначального прогона:** запрошенный 13-сценарный hybrid live выполнен успешно; первоначальный расширенный .NET live оставался красным. Его последующие три PASS описаны в дополнении в начале отчёта. Это не разрешение установки и не устранение исторической неопределённости. Default остаётся C#.
