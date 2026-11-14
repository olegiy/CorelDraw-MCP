# План внедрения гибридного исполнителя CorelDRAW

> Для исполнителя: выполнять по задачам через `superpowers:executing-plans`; в конце — одна независимая проверка результата. Использовать checkbox для хода работ. План рассмотрен: пользователь разрешил выполнение 13 ноября 2026. Коммиты, merge, установка и публикация автоматически не выполняются.

**Цель:** C# сохраняет управление и проверки, готовый VBA runtime выполняет выгодные пакетные операции внутри Corel, а MCP остаётся строго типизированным.

**Архитектура:** один JobManager и один C# STA worker; маршрут выбирается до мутации по фиксированной политике. VBA-модуль загружается из проверенного GMS, используется повторно и не компилируется на каждом вызове. Исходный PowerShell/VBE-исполнитель не восстанавливается.

**Стек:** TypeScript/Node24, C#/.NET10 x64 STA, CorelDRAW27, собственный VBA/GMS.

**Спецификация:** [проектное решение](../../docs/superpowers/specs/2026-11-13-corel-hybrid-runtime-design.md).

**Основание:** [живые испытания](../../docs/live-backend-comparison-2026-11-12.md). Цифры относятся к исследовательским реализациям, а не к ещё не написанному runtime.

## Общие ограничения

- Работать в существующем `codex-like-ui-task-1`, не в корневом checkout 0.2.0. До изменений записать HEAD, git status и SHA файлов C#-миграции/UI; не смешивать и не откатывать прежние изменения.
- Node24; SDK10.0.404; net10.0-windows x64 STA; Corel27.2.0.135; mutex `Local\CorelDrawMcp_v27`.
- Технический backend `csharp` сохраняется; execution_mode/engine показывают реальный маршрут. Не вводить второй процесс-исполнитель или скрытую очередь.
- Нет произвольного VBA/COM dispatch, пользовательского имени макроса, VBE/Compile в runtime и auto-fallback.
- Проверить весь пакет до BeginCommandGroup. Точные document/page/layer IDs и StaticID; locked/ancestor/effects checks сохраняются.
- Polling без COM; таймаут не отменяет native call; unknown/partial никогда не повторяются и не откатываются автоматически.
- Existing tools и лимиты совместимы. Два новых batch-инструмента имеют отдельные пределы:1000items, общий текст100000UTF-16, payload1МиБ, JSONL4МиБ.
- Собственные test documents/private scopes/GMS только; installed runtime не путать с временным диагностическим проектом. Не закрывать/сохранять/удалять рисунки пользователя ради теста.
- Публичные схемы, recipe, host capabilities, installer и docs меняются согласованно. UI не переделывать.
- Все сообщения и отчёты по-русски. Emoji/non-BMP/NUL/lone surrogate отклонять до мутации.
- Право распространения interop не подтверждено: publish/установка готового пакета остаются заблокированными. Локальная разработка и тесты используют установленный Corel и уже имеющийся SDK.

## Фокус независимой проверки

1. Corel/VBA недоступен, включена политика запрета макросов, GMS повреждён или старый: отказ до изменения документа, без ручной компиляции и снижения настроек доверия — задачи2/3.
2. Несколько worker, перезапуск worker, пользователь выгрузил проект: точная идентичность модуля и безопасная работа с общим runtime, без выгрузки чужого проекта — задача3.
3. Неверная последняя запись в1000items, другая страница/единицы/начало координат, заблокированный предок: весь preflight до мутации — задачи4/5.
4. Native failure после нескольких изменений, потеря ответа или deadline: достоверный partial/unknown, сохранение ID, занятый слот и отсутствие второго Apply — задача7.
5. Nonprinting layer и CDR reopen меняют наблюдаемые bounds/metadata: физическая геометрия и явные ограничения без изменения исходного слоя — задачи8/9.

## Карта изменений

| Область | Ответственность и файлы |
|---|---|
| Правила и контракты | `AGENTS.md`, `docs/architecture.md`, `worker/api-contract.json`, `src/backend.ts`, `src/csharp-executor.ts` |
| Native lifecycle | новые `worker/CorelMcp.Worker/Hybrid/{NativeModuleManifest,NativeVbaRuntime,NativeCallCodec}.cs`, `worker/native/module-manifest.json`; изменения `WorkerSession.cs`, `OperationDispatcher.cs` |
| Маршрутизация и подготовка | новые `Hybrid/{HybridPolicy,PreparedNativeBatch,NativeDrawing,NativeBatchContracts,NativeBatchResults}.cs`; изменения `Operations/{DrawingOperation,DrawingContracts,Shapes,Styles,Text}.cs` |
| VBA | новые `worker/vba/{M_Bridge,M_Validate,M_Shapes,M_Text,M_Result,M_Geometry}.bas`; собранный собственный GMS как версионированный build artifact |
| Публичный batch API | новые `src/batch-tools.ts`, `test/batch-tools.test.mjs`, изменения `src/{tools,drawing-tools,guides}.ts`, host catalog/policy и skill references |
| Извлечение геометрии | новый `Hybrid/NativeGeometry.cs`; `Operations/{GeometrySnapshot,Layout}.cs`, при необходимости общий `Operations/TargetResolver.cs`; `src/geometry.ts` сохраняется |
| Пакетирование | новые `scripts/{Build-CorelNativeModule,Verify-CorelNativeModule}.ps1`; изменения `scripts/{Publish-CorelWorker.ps1,worker-package.mjs,Install-CorelMcp.ps1,Install-CorelAgent.ps1,check-contracts.mjs}` |
| Проверки | новые `worker/CorelMcp.Worker.Tests/{HybridPolicyTests,NativeModuleTests,NativeCodecTests,NativeBatchTests,HybridFailureTests,LiveHybridTests}.cs`, `test/integration-hybrid.mjs`, versioned benchmark controller в `scripts/bench-hybrid.mjs` |

Файлы приведены относительно рабочего дерева. Не переносить из `.superpowers/sdd` весь исследовательский bridge: использовать его как доказательство API и fixtures, переписать production-путь на существующих защитных контрактах.

## Задача1. Зафиксировать базу и границы маршрутизации

**Файлы:** `AGENTS.md`, `docs/architecture.md`, новый `Hybrid/HybridPolicy.cs`, `HybridPolicyTests.cs`, `test/backend.test.mjs`.

**Интерфейсы:**

- Потребляет существующие `IDrawingCommand`, `DrawingTarget`, WorkerSession.
- Создаёт `ExecutionMode { CSharp, Hybrid }`, `ExecutionEngine { CSharp, Vba }`, `RouteDecision(ExecutionEngine Engine, string Operation, string PolicyVersion)` и `HybridPolicy.Select(IDrawingCommand command, TargetSummary targets, ExecutionMode mode)`.
- `TargetSummary` содержит проверенные типы/количество/результирующее число копий; сбор сведений не мутирует документ. PolicyVersion=`hybrid-v1`.

- [x] Снять base manifest. Старые dirty файлы оставить нетронутыми; выделить разрешённый список файлов этой работы.
- [ ] Написать `HybridPolicyTests`: mode=csharp всегдаCSharp; batch всегдаVba вhybrid;20simple create/style/move/duplicate→Vba;19→CSharp; неподдержанная структура существующей операции сохраняет заранее выбранный CSharp; новаяbatch сgroup отклоняется; маршрутизация не принимает outcome/error предыдущего вызова.
- [ ] RED: `dotnet test worker/CorelMcp.Worker.Tests --filter FullyQualifiedName~HybridPolicyTests` — FAIL из-за отсутствующего нового policy.
- [ ] Реализовать policy и явно заменить запрет любого VBA в AGENTS на разрешение только фиксированного runtime. Сохранить запрет произвольного кода, fallback и runtime-VBE.
- [ ] GREEN: тот же filter плюс `node --test test/backend.test.mjs`; старый `CORELDRAW_MCP_BACKEND=csharp` работает, удалённый backend не восстанавливается. Пока default execution_mode=csharp.

**Выход:** однозначная таблица маршрутов и проверенная база, без переключения production.

## Задача2. Получить готовый GMS и доказать запуск без редактора

**Файлы:** `worker/vba/M_Bridge.bas`, `worker/native/module-manifest.json`, `NativeModuleManifest.cs`, `NativeModuleTests.cs`, новые Build/Verify scripts; `worker/api-contract.json`.

**Интерфейсы:**

- `NativeModuleManifest.LoadAndVerify(string directory) -> VerifiedNativeModule` с protocol1, точным project name/path, source/GMS SHA256, Corel27 и списком operation IDs.
- `M_Bridge.Handshake(nonce As String) As String` возвращает strict JSON:protocol, nonce, module version, source hash, corel major и supported_operations. Объектов рисунка handshake не принимает.
- Проект `CorelMcp_Runtime_v1_<source-hash-prefix>`. Source hash считается по tracked BAS; генерируемый build-info фиксированного шаблона учитывается отдельно, чтобы не получить круговую зависимость хеша.

- [x] Написать RED-тесты missing/tampered manifest/GMS, path escape/link, wrong version/hash; ни один не должен доходить до RunMacro. Документу не приписывать мутацию при отказе загрузки.
- [x] Реализовать manifest verification и developer-only сборку собственного проекта. Dev-инструмент не вызывает installed/user GMS. Компиляция/сохранение через VBE/agent-desktop допускаются только на этапе сборки; обычный `npm build` не запускает Corel и не создаёт GMS.
- [x] `dotnet test worker/CorelMcp.Worker.Tests -c Release --filter FullyQualifiedName~NativeModuleTests` — PASS. Сохранить source/typelib/build provenance; не обещать побитовую воспроизводимость файла, если Corel добавляет метаданные.
- [x] Live gate: `Verify-CorelNativeModule.ps1 -ModulePath <own.gms> -RunDirectory <own-dir>` проверяет Load(CopyFile=false,ForAllUsers=false), integrity lock, handshake и фиксированную функцию без VBE/Compile/manual marker. Нужен opt-in live стенд.
- [ ] Повторить20загрузок/привязок, запуск из нового worker и после нового Corel на стенде с baseline documents=0. При наличии открытых исходных документов не перезапускать Corel; отложить только эту часть gate с явным статусом.
- [ ] При macro policy/VBA-unavailable/collision — ожидается явный код до document mutation. Если готовый GMS не работает без ручной компиляции, задача не пройдена; не подключать старый адаптер как обход.

**Выход:** проверенный формат/артефакт готового runtime. Это первый go/no-go всей миграции.

## Задача3. Встроить lifecycle и ограниченный native bridge в C#

**Файлы:** `NativeVbaRuntime.cs`, `NativeCallCodec.cs`, `WorkerSession.cs`, `OperationDispatcher.cs`, `NativeCodecTests.cs`, `NativeModuleTests.cs`.

**Интерфейсы:**

- `NativeVbaRuntime.EnsureReady(OperationContext c) -> NativeModuleIdentity`, `ValidateFonts(string[] names) -> FontValidation`, `Apply(PreparedNativeBatch batch) -> NativeReply`.
- `NativeReply`:protocol, nonce, operation, ok, error, applied(bool?), partial, affected/created/deleted IDs, ordered items. Unknown fields и неверные типы отклоняются.
- Единственный приватный вызов `RunMacro`: project изVerifiedNativeModule, macro из фиксированного enum→константа mapping, параметры object[]/SAFEARRAY и BSTR. Аргументы никогда не вставляются в исходный код/командную строку.

- [x] RED-тесты nonce/version/op mismatch, truncation/overflow, malformed JSON, русская locale/Unicode/quotes/newlines, fake module с тем же именем по другому пути.
- [x] Реализовать bootstrap/привязку под existing STA/mutex. Runtime статeless; одинаковый установленный модуль может использоваться несколькими worker. Не выгружать общий GMS при обычном dispose; освобождать RCW своего worker. Isolated test project выгружать точно по manifest/identity.
- [x] `dotnet test worker/CorelMcp.Worker.Tests -c Release --filter "FullyQualifiedName~NativeCodecTests|FullyQualifiedName~NativeModuleTests"` — PASS.
- [x] Live: два worker поочерёдно используют один штатный модуль; завершение одного не ломает второй. Выгрузка/подмена модуля пользователем определяется до следующего Apply; не появляется копия/похожий проект.
- [x] Добавить status.execution_mode/native availability и job versions.execution_engine/route_policy/native_module. Polling/get_job всё ещё не вызывает COM; status показывает только подтверждённое состояние, не гарантирует вечную готовность.

**Выход:** приватный bridge без публичной возможности выбрать/вызвать макрос.

## Задача4. Добавить два строгих batch-контракта и согласовать host

**Файлы:** `src/batch-tools.ts`, `src/tools.ts`, `NativeBatchContracts.cs`, `OperationContracts.cs`, `DrawingContracts.cs`, `test/batch-tools.test.mjs`, `NativeBatchTests.cs`, host `policy.py`/catalog, skill tools/recipes и `scripts/check-contracts.mjs`.

**Интерфейсы:**

- `corel_create_batch`: общая binding/layer; `items[1..1000]`, уникальный item_key длиной1–100символов; rectangle/ellipse/line/artistic_text, optional none/uniform style, rotation_deg вокруг собственного центра.
- `corel_update_batch`: binding; `items[1..1000]`, уникальный static_id, хотя бы fill/outline/одна transform. Порядок style→transform. Только простые leaf targets без эффектов/PowerClip/bitmap/group.
- `NativeBatchContracts.Parse(tool, JsonElement) -> IDrawingCommand`; имена новых command records `CreateBatchCommand`, `UpdateBatchCommand`. Общий объём текста100000UTF-16/payload1МиБ проверяется до запуска; JSONL4МиБ не меняется.

- [x] RED:1001item, duplicatekey/id, unknownfield/macro/code/backend selector, missingfont, NUL/non-BMP/lone-surrogate/NaN/Infinity, смешанная несовместимая геометрия, переполнение общего текста. Последний неверный элемент отклоняет пакет целиком.
- [x] Реализовать одинаковые unions/limits вTS иC#. Existing schemas/limits сохранить; host считает batch мутацией и применяет существующую политику доступа, без специального обхода подтверждений.
- [x] `npm run build`; `node --test test/batch-tools.test.mjs test/drawing-tools.test.mjs`; C# filter NativeBatchTests — PASS.
- [x] Обновить capability catalog/fixture/skill metadata/JSON recipes; `npm run check:contracts` — PASS. Новый набор содержит прежние37+2инструмента, без execute_vba.

**Выход:** синхронные строгие контракты; это не универсальный batch script.

## Задача5. Реализовать пакетную геометрию/стили и общий безопасный resolver

**Файлы:** `PreparedNativeBatch.cs`, `NativeDrawing.cs`, `NativeBatchResults.cs`, `M_Validate.bas`, `M_Shapes.bas`, `M_Result.bas`; `DrawingOperation.cs`, `Shapes.cs`, `Styles.cs`, при необходимости `TargetResolver.cs`.

**Интерфейсы:**

- `NativeDrawing.Prepare(DrawingTarget target, IDrawingCommand command) -> PreparedNativeBatch` до BeginCommandGroup; native Apply вызывается существующим C# CommandGroup/StateScope.
- PreparedNativeBatch содержит nonce/operation, уже разрешённые Document/Page/Layer/ShapeRange, нормализованные координаты и SAFEARRAY rows, ожидаемую структуру результата. Освобождается на том же STA, не удерживается VBA между командами.
- NativeBatchResults проверяет ответ и заполняет существующий DrawingResult; mandatory result собирается внутри Corel, не вызывает target.Info тысячу раз через COM.

- [ ] RED: wrong document/page/layer;1invalid из1000; locked shape/ancestor; group+child;speciallayer;effects; разные mm/inch/origin/referencepoint; staleID; неправильный order/key/ID вответе. До group/sourcechanges должно быть0.
- [x] Реализовать один resolver/preflight на пакет с сохранением ancestry guards. Не переносить плоский FindShape shortcut из исследования. Сначала общий индекс дерева один раз; иной быстрый способ — только если доказывает те же проверки.
- [x] Реализовать create/update и подготовленные маршруты existing create_shapes/style/move/duplicate. ShapeRange использовать только при совпадении семантики. Порядок операций и unit conversion — как в спецификации.
- [x] C# контролирует Begin/End, settings/selection/Dirty; VBA только применяет и возвращает проверенные data/IDs. Никаких Undo/перехода в другой backend при ошибке.
- [ ] Unit/native fixture tests PASS; live20/100: полная проверка каждой цели и источника отдельным читателем, совпадение старого publicDTO где он сохраняется. Public IDs после копирования/convert не угадываются.

**Выход:** настоящий production batch-path без повторного обхода/подробного snapshot через внешний COM на каждую фигуру.

## Задача6. Перенести выгодные текстовые пакеты с сохранением семантики

**Файлы:** `M_Text.bas`, `M_Validate.bas`, `NativeDrawing.cs`, `Operations/Text.cs`, `Validation/CorelText.cs`, `TextTests.cs`, `LiveHybridTests.cs`.

**Интерфейсы:**

- Native ValidateFonts выполняет один проход внутри Corel по уникальным именам текущего пакета; не хранит постоянный font cache.
- Фиксированные operation IDs: CreateBatch (включая artistic_text), ReplaceLiteral, SetText, FormatText, ConvertText. Existing edit/find contracts сохраняются; Find остаётся C#.
- `corel_create_text` одиночный не сливается с соседними jobs; массовое создание идёт через create_batch и batch-рецепт.

- [ ] RED:BMP/кириллица, кавычки/newlines, literal match order/case, смешанное форматирование вне замены, длинный текст, недоступный шрифт, фактическая подстановка, paragraph/unsupported source, partialconvertID mappings.
- [x] Реализовать проверку всего текста/диапазонов/шрифтов до group. Replace сохраняет обратный порядок диапазонов и существующую ordinal-семантику; не заменять её locale-зависимым InStr/vbTextCompare из пилота без эквивалентного gate.
- [x] Реализовать нативное создание/правки/convert для таблицы маршрутов; actual font/readback проверяется, известные созданные ID сохраняются при FONT_SUBSTITUTED.
- [ ] C# Text/CorelText/NativeBatchTests PASS; live каждый из20/100текстов проверен по тексту/family/size/bold/italic/геометрии и результату convert, не только по count.

**Выход:** ускорение текста с полноценными guards, а не перенос тестовой функции с hardcoded Arial/«Ёж».

## Задача7. Зафиксировать partial/unknown и отказоустойчивость

**Файлы:** `HybridFailureTests.cs`, `OperationDispatcher.cs`, `WorkerSession.cs`, `src/{csharp-executor,jobs,worker-results}.ts`, соответствующие process/jobs/host recovery tests; private test-only native fault fixture.

**Интерфейсы:** использует текущие WorkerError.applied, DrawingResult.partial, request_id/job_id и pending/native completion; не добавляет публичный fault selector.

- [ ] RED: ошибка preflight до BeginCommandGroup→appliedfalse; сбой после BeginCommandGroup без подтверждения изменений→консервативный appliednull; после2из5knownchanges→partial+подтверждённыеID; неверный/потерянный ответ послеApply→unknown;cleanupfailureнеуспех;deadlineнеосвобождаетслот;repeatedrequestid→одинApply.
- [x] Сделать fault injection через unit double/отдельный тестовый GMS, отсутствующий в shipping manifest. Не ломать пользовательские рисунки и не завершать production Corel ради unit-теста.
- [x] Согласовать partial native reply с RecordMutation/DrawingItems; при неизвестном диапазоне изменений не объявлять appliedfalse и не записывать ложный полный список.
- [ ] C#/Node process/jobs tests PASS, host recovery tests PASS. Live controlled partialfault на собственном документе подтверждает изменённые ID и восстановление state; runtime не делает automaticUndo.
- [ ] Клиент отключается/подключается и читает существующий report без второго Apply. Старые retained GMS/unknown reports не переписываются и не проигрываются новой версией.

**Выход:** таймаут/ошибка не превращают ускорение в повторное изменение рисунка.

## Задача8. Ускорить geometry extraction и разобраться с nonprinting bounds

**Дополнение по живому тесту H11:** 13 ноября финальный C# geometry100 завис на чтении `StaticID` исходного RCW после `CreateDocumentFrom`. Deadline не освободил слот, повтор не выполнялся. Стек и сверка 100 исходных фигур сохранены. Реализован bounded immutable proof/ID snapshot до клонирования для measure/export/layout; исходные shape-getters после клонирования исключены. Повторный live100 прошёл прогрев и 3 пары без зависания, с проверкой всех целей и неизменности источника: отчёт `a571a070-bb14-4166-82ee-cd3f1c10b73a`.

**Дополнение H12:** регрессия после исправления H11 выявила переключение активного документа при позднем освобождении SVG-фильтра. ExportFilter/PDFSettings освобождаются до восстановления внешнего состояния. Проверка активного документа после каждого из девяти форматов, а также геометрия и layout в мм/дюймах прошли (`geometry-export-cache-green.log`, 3 теста). Ошибки native geometry дополнительно проверяются на правильный execution_engine и сохранность источника.

**Файлы:** `Hybrid/NativeGeometry.cs`, `M_Geometry.bas`, `GeometrySnapshot.cs`, `Layout.cs`, `LiveGeometryExportTests.cs`, `LiveLayoutTests.cs`, `src/worker-results.ts` только при необходимости metadata.

**Интерфейсы:** `NativeGeometry.Extract(OperationContext c, VGCore.Document privateDocument, IReadOnlyList<GeometryTargetMap> targets, double unitToMm) -> string snapshotPath`; `GeometryTargetMap(int SourceStaticId, VGCore.Shape PrivateShape)`; прежний JSON objects/segments и TypeScript measureSnapshot остаются совместимыми. Путь нового artifact генерирует C#, не пользователь; лимит64МиБ и existing point limits сохраняются.

- [ ] RED:10/100 фигур со stroke, эллипсы/Bezier/group, исходник Dirty/selection неизменны, private cleanup; nonprinting layer с фактически ненулевой фигурой; reopen при unit=1; stale layout.
- [ ] Сначала воспроизвести H05 и установить причину. Не чинить её переключением Printable исходника. При невозможности достоверных bounds вернуть UNSUPPORTED_LAYOUT_BOUNDS с причиной до apply вместо ложного ZERO_SIZE.
- [ ] Реализовать извлечение в уже принадлежащем C# private document внутри Corel: оформление stroke/convert по прежнему контракту, bounded UTF-8 `.part`→готовый artifact; source не конвертируется/не дублируется на месте. Писатель JSON/UTF-8 фиксирован, locale-independent, без произвольных file operations.
- [ ] Все macro locals/RCW private объектов освобождены до privateDocument.Close; ранее исправленный import/crash regression остаётся зелёным.
- [ ] Сравнить оба snapshot пути одной math-функцией и физическими допусками; `original_state_preserved=true` только при подтверждённом cleanup. Live geometry100:3повтора, улучшение>=2x для переключения этой операции наVBA. Если gate не пройден — явно оставить данный маршрутC#, не менять его после ошибки.

**Выход:** улучшенный snapshot/честные bounds; отсутствие выигрыша не выдаётся за выполненную оптимизацию.

## Задача9. Исправить демонстрации, диагностику стилей и сравнительный стенд

**Файлы:** `scripts/bench-hybrid.mjs`, `test/integration-hybrid.mjs`, новый `scripts/Show-CorelBenchmark.ps1`, `Operations/Export.cs`, capability/guide/docs; reuse копий собственных fixtures из live report.

**Интерфейсы:** Show-helper принимает точные PID/document path, получает свежий session-scoped document_id и проверяет его в той же MCP-сессии; выполняет только показ собственного benchmark fixture. Результаты benchmark: manifest SHA, параметры, route, samples, resolve/apply/snapshot/postcheck/save/total, correctness и cleanup.

- [ ] Заменить предположение «Activate/Refresh успешен — холст виден» на observe-act-verify agent-desktop; после наблюдаемого пустого viewport разрешён явный refresh/zoom с новым снимком. Обычный worker не переводит фокус на каждом запросе.
- [x] H06: отдельный private texture probe подтверждает заданный preset/native identity; mismatch записывается какunsupported/presetunconfirmed и не изменяет целевой документ. Не добавлять общий texture-инструмент до подтверждения API/selector.
- [x] H07: подтвердить pattern→SVG rasterization парным артефактом и добавить конкретное предупреждение для распознанного случая. Если полнота определения не гарантируется, явно выдавать unknown fidelity, не `vector_preserved=true` и не «исправлять» формат другим расширением.
- [ ] H09/H10: физическиеmmпослеreopen, раздельные геометрические bounds/outline bounds, допуски угла0.001° и геометрии0.001мм для этихfixtures, внешнийFilePathнеявляетсяэталономappearance, cdrPATnotexportcapability. Для каждой временной итерации проверить все цели вне timing; PNGdiff и PDFcontent проверять явно, не только header.
- [ ] Запустить пары20/100/1000C#↔hybrid с чередованием порядка:плашкиRGB0.5%/поворот1°,20/100текстов,style/move/duplicate,geometry100,PNG/inspection. Прогрев+5быстрых или3медленных повторов; новый случай<=5мин с кооперативной остановкой. Старый зависающий frozen adapter не запускать как shipping backend.
- [ ] Gates:1000плашекtotalhybrid<=30с и>=2xбыстрееC#;20текстов<=2с и>=3x; PNG/inspection без повторяемойрегрессии>20%. Числа относятся к полному одинаковому сценарию; pureapply не подменяет total.
- [ ]100nativeопераций длительного smoke без hang/утечекGMS/документов. Один атлас/100этикеток/10страниц/векторрезки: функциональная проверка и видимый показ. Неподдержанные mesh/spot/liveeffects остаются честными capability gaps.

**Выход:** повторяемые измерения нового кода и исправленный контроль качества стенда, с сохранением всей диагностики ошибок.

## Задача10. Комплектность, переключение default и итоговая приёмка

**Файлы:** scripts package/install/check-contracts; `src/{backend,version}.ts`, package/version files, `worker/native/module-manifest.json`, `worker/api-contract.json`, AGENTS/README/architecture/skill и host capability table; новый `docs/verification-hybrid-0.4.0.md`.

**Интерфейсы:** native manifest/source/GMS hashes входят в worker-manifest; installer проверяет VBA availability/модуль/версии и право interop до активации. Rollback — выбор старого неизменённого комплекта/режима для новых запросов, без replay jobs.

- [ ] RED package tests:missingGMS/hashmismatch/nativeversionmismatch/лишнийruntimeVBEdependency/неподтверждённыеinteroprights→активацияневозможна; fixture tests не обходят реальные rights-gates.
- [ ] Реализовать упаковку своего модуля и notices; version0.4.0 по всем потребителям. Не удалять сторонние/старые неизвестныеGMS. Предыдущий рабочий комплект сохраняется до проверки нового.
- [ ] Полный gate последовательно: `npm test`, `dotnet test worker/CorelMcp.Worker.Tests -c Release` безliveenv, `npm run check:contracts`, затем opt-in live `npm run test:integration` и `node test/integration-hybrid.mjs`. Не запускать npm mutex test одновременно с liveCOM.
- [x] Существующие host tests, затронутые policy/catalog/recovery, проходят; проверить OpenAPI только если publicHTTPконтракт действительно изменён. UI менять только в необходимых сгенерированных типах/информацииcapabilities, без redesign.
- [ ] После выполнения задач2–9 и performance/safety gates изменить default execution_mode наhybrid. При непрохождении gate default остаётсяcsharp и задача приёмки не объявляется complete.
- [ ] Один независимый финальный review diff/спецификации/reports; исправить materialfindings и повторить затронутые проверки. Сохранить knownlimits/непроверенныережимы47групп.
- [ ] Сверить baselineконтрольныхдокументов/настроек. Удалить только собственные временные diagnosticGMS, сохранив source/manifests/reports; общему установленному runtime и пользовательским документам не вредить. Показательные макеты оставить открытыми для пользователя.
- [ ] Итог: списокизменённыхфайлов, реальныекоманды/результатыtests, сравнение скоростей, остаточныеограничения и правоinterop. Не коммитить/merge/publish/install автоматически.

**Выход:** проверенный гибридный код и локально проверяемый комплект; распространение остаётся отдельным gate по правам.

## Порядок и критерий остановки

Задача1→2→3→4→5→6→7; затем8→9→10. Исследовательские проверки H06/H07 не расширяют первую поставку до всех сложных стилей. При невозможности готового GMS без runtime-компиляции остановить внедрение перед переключением маршрутов и оформить конкретный результат; не возвращать старый зависающий путь.

План считается выполненным только при прошедших correctness/safety gates и опубликованных фактических измерениях нового кода. Частично реализованный runtime, один успешный пилот или зелёная схема без живого запуска не равны готовой гибридной версии.



## Самопроверка плана

- Архитектура и отсутствие runtime-компиляции: задачи1–3; строгие batch-контракты и host: задача4; исполнение/текст:5–6; safety/unknown:7; геометрия:8; наблюдаемые результаты/benchmark:9; комплектность:10.
- Исправления H01→2/3/7, H02→5/8, H03→6, H04→9, H05→8, H06/H07→9, H08→4/6, H09/H10→9. Каждая строка имеет проверку результата, а ограничения Corel отделены от дефектов реализации.
- Пять условий Review Focus привязаны к RED/live тестам конкретных задач. Интерфейсы policy/manifest/runtime/prepared batch/native reply/geometry mapping определены до использования.
- Решения, требующие доказательства, оформлены как gates: запуск готового GMS без редактора; равенство guards; partial/unknown; полнота результатов; фактическое ускорение. Недоказанный gate не заменяется ручной процедурой или тихим fallback.
- План и спецификация созданы; production-код, настройки Corel, установленный runtime и пользовательские макеты при подготовке плана не изменялись.

## Незакрытые возможности

- **Сетчатая заливка — mesh:** путь создания и редактирования через API пока не подтверждён.
- **Spot-цвета и overprint:** отсутствуют в текущем строгом MCP-контракте.
- **Трансформации некоторых сложных эффектов:** текущий MCP безопасно отказывает.
