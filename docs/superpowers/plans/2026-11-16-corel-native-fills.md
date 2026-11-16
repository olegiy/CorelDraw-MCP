# Corel Native Fills & Effects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить в corel-mcp нативные типизированные операции CorelDRAW 27: многостоповые fountain-заливки в `corel_style_shapes`, новые `corel_set_transparency` и `corel_apply_drop_shadow`, сохранив все существующие барьеры и контракты.

**Architecture:** Аддитивный kind `fountain` в союзе fill (TS zod `src/drawing-tools.ts` → C# `FillSpec` → `Styles.cs` COM-apply → read-back в `Inspection`/`DrawingOperation.Info`); два новых инструмента по шаблону `corel_powerclip` (zod → DTO-запись → `DrawingOperation` switch → отдельный `Operations/*.cs` с preflight-до-мутации). Hybrid-пакеты получают отдельный союз `batchFill` (none|uniform) и явный отказ fountain в worker до маршрутизации.

**Tech Stack:** TypeScript (zod 4, Node 24), C# .NET 10 x64 STA worker (VGCore.Interop 27.2), xunit, node --test, Windows PowerShell 5.1 scripts.

**Spec:** `docs/superpowers/specs/2026-11-16-corel-native-fills-design.md`

## Global Constraints

- Node.js 24 (`engines >=24 <25`), SDK 10.0.404 закреплён `global.json`; worker только csharp backend; hybrid-режим не включаем.
- Никаких логов в stdout сервера; аргументы не через shell/Invoke-Expression; без Roslyn/универсального COM Invoke/fallback.
- Все COM на одном STA под `Local\CorelDrawMcp_v27`; polling не вызывает COM; deadline не отменяет работу; после timed_out/unknown/partial — без replay/undo.
- Валидация всех целей/значений до первой мутации, включая BeginCommandGroup; locked/special layer, bitmap, unsupported effects/PowerClip — отказ до мутации.
- Точные document_id/page_index/layer_id/StaticID; без выбора по имени/индексу; без Application.Quit; live-тесты создают/закрывают только свои временные документы.
- Сохраняемые барьеры: `DrawingOperation.cs:56–59` allowTransparency whitelist, `:115–125` effects gate (единственный cdrLens unfrozen uniform/fountain), `GeometrySnapshot.cs:100,103`, `NativeDrawing.cs:66–100` NATIVE_OUTCOME_UNKNOWN, historical DTO/reports без replay.
- Mesh — non-goal (нет в dispatch API 27); pattern/texture/inner shadow/blend/extrude — non-goal.
- Единицы: координаты/ширины — мм от нижнего левого угла; `center_offset_*_percent` — проценты объекта, БЕЗ конвертации; angle для linear/conical/square, radial — отказ.
- Стопы fountain: крайние 0/100 фиксированы, промежуточные 1..99 строго возрастают/уникальны; 2..16 стопов.
- Цели тени v1: одиночные фигуры types 1/2/3/4 + текст 6; группы — отказ (limitation v1).
- Счётчик инструментов синхронно в трёх местах при каждом добавлении: `skills/coreldraw-automation/references/tools.md:5`, `webhost/tests/test_transport_compatibility.py:12`, `worker/api-contract.json:3097` (39→40→41).
- `scripts/check-contracts.mjs` сверяет dist-zod ↔ tools.md (строки таблицы, JSON-описания, jsonl-метаданные) и хардкод-список из 12 рецептов; валидация всех `examples/*.json` по каталогу живёт в `test/recipe-contract.test.mjs` → обе поверхности правятся В ТОМ ЖЕ задании, что и схема (новые examples добавляются и в хардкод-список check-contracts).
- **Коммиты и установка в пользовательское окружение ЗАПРЕЩЕНЫ без отдельного разрешения пользователя.** Шаги «Commit» заменены на «Checkpoint»: зелёные тесты + отметка в плане; git-коммиты не делать. В конце execution спросить разрешение на коммит отдельным сообщением.
- Live suites последовательно; `test/mutex.test.mjs` не параллелить с live COM.

## Review Focus

- Percent-поля центра в inch-документе: значение передаётся как есть, без `target.U()`; градиент не «уезжает» на порядок (worker unit + live read-back).
- Стопы вне 0..100 / дубликаты / невозрастающие / <2 / >16: отказ до мутации, Dirty документа не меняется (node + worker contract tests).
- Fountain в `corel_create_batch`/`corel_update_batch`: отказ схемой TS И отказ worker до маршрутизации даже в hybrid-окружении (batch tests обоих уровней).
- Тень на группе/bitmap/locked/уже затенённой цели: отказ до мутации; при успехе `id_mappings` old→new точен, старый ID исчезает, потомки сохраняются (live test).
- Прозрачность: frozen lens / locked / special layer — отказ; `applied_to=outline` читается обратно; style/convert/boolean/scale на прозрачной цели по-прежнему отказаны (барьер LiveEffectsTests сохраняется).

## File Structure

- Modify `src/drawing-tools.ts` — союз fill += fountain; определения двух новых инструментов; описания.
- Modify `src/batch-tools.ts` — собственный `batchFill` (none|uniform) вместо общего fill.
- Create `test/effects-tools.test.mjs` — схемы transparency/shadow, отказы до executor.
- Modify `test/drawing-tools.test.mjs`, `test/batch-tools.test.mjs`, `test/drawing-catalog.test.mjs` — fountain/счётчик.
- Modify `worker/CorelMcp.Worker/Operations/DrawingContracts.cs` — FountainFill/FountainStop записи, валидация, whitelist имён, switch.
- Create `worker/CorelMcp.Worker/Operations/Transparency.cs`, `Operations/DropShadow.cs` — preflight + COM-apply + observe.
- Modify `worker/CorelMcp.Worker/Operations/Styles.cs` — fountain-ветка; `Operations/DrawingOperation.cs` — switch + Info read-back; `Operations/Inspection.cs` — fountain read-back.
- Modify `worker/CorelMcp.Worker/Hybrid/NativeBatchContracts.cs` — отказ fountain в Style.
- Modify `worker/CorelMcp.Worker.Tests/DrawingContractsTests.cs`, `NativeBatchTests.cs`; Create `LiveNativeFillsTests.cs`.
- Modify `worker/api-contract.json` — signatures/enums/live_status/catalog_tools.
- Modify `test/integration-drawing.mjs`, `test/integration-hybrid.mjs` — fountain live-кейсы.
- Modify `skills/coreldraw-automation/references/tools.md` (3 блока + счётчик), `references/operations.md`, `references/pitfalls.md`, `SKILL.md`; Create `skills/coreldraw-automation/examples/fountain-fill.json`, `examples/transparency-shadow.json`; Modify `references/recipes.md`.
- Modify `webhost/corel_agent/policy.py` (DRAWING_MUTATIONS), `webhost/tests/test_transport_compatibility.py`, `webhost/tests/test_typed_policy.py`; regen `webhost/tests/fixtures/catalog.mjs`.
- Modify `docs/architecture.md`, `worker/README.md`, `skills/coreldraw-automation/references/api-v27.md`, `scripts/check-contracts.mjs` (хардкод-список рецептов).

---

### Task 1: Изолированный worktree и зелёный baseline

**Files:** none (окружение).
**Interfaces:** Produces: worktree-каталог для всех следующих задач; baseline-отчёт.

- [ ] **Step 1:** Создать worktree через superpowers:using-git-worktrees от ветки `14-11-2026` (имя `native-fills`); сделать его рабочей директорией сессии (opencode session_move).
- [ ] **Step 2:** Baseline: `npm ci --ignore-scripts`; `npm test`; `npm run check:contracts`; `dotnet test worker/CorelMcp.Worker.Tests -c Release` (non-live). Expected: всё зелёно, 39 инструментов. Фактический baseline (commit 17efbdf): node 170 = 168 pass / 0 fail / 2 skip; contracts ok; dotnet 250 = 180 pass / 0 fail / 70 skip (live/COM). Prerequisite окружения: `pwsh -File scripts/build-worker.ps1 -GenerateInteropOnly` (генерация VGCore.Interop) до первого dotnet build.
- [ ] **Step 3:** Checkpoint: зафиксировать baseline-числа тестов в плане; коммит docs разрешён пользователем (ветка native-fills).

### Task 2: Fountain-схема TS + барьер batch-схемы

**Files:** Modify `src/drawing-tools.ts:26–32,140–141`; Modify `src/batch-tools.ts:3–8`; Modify `test/drawing-tools.test.mjs`, `test/batch-tools.test.mjs`; Modify `skills/.../references/tools.md` (строка style :38, JSON-описание :149, jsonl :198).
**Interfaces:** Produces: zod `fountainFill` = strictObject{kind:'fountain', type: enum(linear|radial|conical|square), stops: array(min2,max16).of(strictObject{position:int, color}), angle_deg: number.optional(), center_offset_x_percent: number.optional(), center_offset_y_percent: number.optional(), spread: enum(pad|reflect|repeat).optional(), steps: int.optional(), edge_pad: int.optional(), mid_point: int.optional(), smooth_blend: bool.optional(), blend_type: enum(...).optional()} с refine: stops[0].position==0 && last==100, промежуточные 1..99 строго возрастают; angle запрещён при radial; center только radial/square/conical. Экспорт `fill` включает fountain; `batchFill` = none|uniform (экспорт из drawing-tools для batch).

- [ ] **Step 1:** Failing tests в `test/drawing-tools.test.mjs`: валидный 3-стоповый linear проходит `schema.parse`; невалидные (position 101, дубликат 50/50, невозрастающие 0/70/30/100, 1 стоп, 17 стопов, angle при radial, center при linear) → throw; `test/batch-tools.test.mjs`: create/update item с fill.kind='fountain' → throw.
- [ ] **Step 2:** `npm test` → ожидаемо FAIL (новые кейсы).
- [ ] **Step 3:** Реализовать союзы/refine в `src/drawing-tools.ts`; в `src/batch-tools.ts` заменить `fill` на `batchFill`; обновить описание style в tools.md (три блока) так, чтобы check-contracts прошёл.
- [ ] **Step 4:** `npm test` и `npm run check:contracts` → PASS; `test/drawing-catalog.test.mjs` ещё 39.
- [ ] **Step 5:** Checkpoint (без коммита).

### Task 3: Worker FountainFill DTO + валидация + batch-барьер

**Files:** Modify `worker/CorelMcp.Worker/Operations/DrawingContracts.cs:31–35,74,85–94,158–162`; Modify `Hybrid/NativeBatchContracts.cs:36–41`; Modify `CorelMcp.Worker.Tests/DrawingContractsTests.cs`, `NativeBatchTests.cs`; Modify `src/drawing-tools.ts` (blend_type enum: direct|rainbow_cw|rainbow_ccw|custom вместо placeholder ['direct']) и `skills/.../references/tools.md` если enum фигурирует в блоках; Modify `test/drawing-tools.test.mjs` (blend_type кейсы).
**Interfaces:** Consumes: TS-схему Task 2 (snake_case поля). Produces: записи `FountainStop(int Position, ColorSpec Color)`, `FountainFill(string Type, FountainStop[] Stops, double? AngleDeg, double? CenterOffsetXPercent, double? CenterOffsetYPercent, string? Spread, int? Steps, int? EdgePad, int? MidPoint, bool? SmoothBlend, string? BlendType) : FillSpec` с `[JsonDerivedType(typeof(FountainFill),"fountain")]`; validation-метод `ValidateFountain(FountainFill)` (диапазоны/стопы/применимость); код отказа `BATCH_FILL_UNSUPPORTED`; blend-mapping direct→cdrDirectFountainFillBlend(0), rainbow_cw→1, rainbow_ccw→2, custom→3.

- [ ] **Step 1:** Failing xunit: валидный fountain парсится `DrawingContracts.Parse("corel_style_shapes",…)`; невалидные стопы/углы/центры → preflight-ошибка applied=false; batch payload с fountain → `BATCH_FILL_UNSUPPORTED` до мутации.
- [ ] **Step 2:** `dotnet test … --filter DrawingContractsTests|NativeBatchTests` → FAIL.
- [ ] **Step 3:** Реализовать записи/валидацию/switch/барьер (барьер в `NativeBatchContracts.Style` проверяет kind до `ParseExisting`).
- [ ] **Step 4:** `dotnet test` non-live → PASS.
- [ ] **Step 5:** Checkpoint.

### Task 4: COM-apply fountain + read-back + live-гейт заливки

**Files:** Modify `Operations/Styles.cs:15–19,31–36`; Modify `Operations/DrawingOperation.cs:146–161`; Modify `Operations/Inspection.cs:68–72`; Create `CorelMcp.Worker.Tests/LiveNativeFillsTests.cs` (fountain-часть); Modify `test/integration-drawing.mjs`.
**Interfaces:** Consumes: `FountainFill` Task 3; `DrawingTarget.Resolve/Validate/Observe`, `c.Track/ c.Mutating`, `App.CreateRGBColor/CreateCMYKColor`. Produces: read-back `fill_type=2` + `fountain:{type,angle_deg,center_offset_x_percent,center_offset_y_percent,spread,stops:[{position,color}]}` в items inspect/integration.

- [ ] **Step 1:** Failing live test (`LiveCorelFact`, коллекция CorelLive): свой документ, rect+ellipse+curve; style fountain 3 стопа linear angle 45; assert items fill_type==2 и stops read-back равны заданным (±0 позиции); radial с center 20/-30 read-back равен; inch-документ (unit меняется test-only adapter-ом существующего DrawingClient, если доступен, иначе mm-документ + unit-assert отсутствия конвертации в worker unit); Dirty/cleanup.completed==true; закрыть только свой документ.
- [ ] **Step 2:** `CORELDRAW_WORKER_LIVE=1 dotnet test --filter LiveNativeFillsTests` → FAIL (операция ещё не применяет fountain).
- [ ] **Step 3:** В `Styles.cs`: цвета всех стопов создать ДО замыкания; `ApplyFountainFill(first,last,type,angle,steps??256,edgePad??0,midPoint??50,blendType,centerX,centerY)`; промежуточные `Colors.Add`; percent-поля передавать как есть (без `target.U()`); read-back в Info/Inspection через `Fill.Fountain` (Type/Angle/CenterOffset*/SpreadMethod/Colors enumeration).
- [ ] **Step 4:** Live filter → PASS; `npm run test:integration` (drawing-кейс fountain) → PASS; node integration assert read-back.
- [ ] **Step 5:** Checkpoint.

### Task 5: corel_set_transparency (схема → DTO → COM → read-back → sync 40)

**Files:** Modify `src/drawing-tools.ts` (определение + описание); Create `test/effects-tools.test.mjs`; Modify `Operations/DrawingContracts.cs` (whitelist/switch/валидация); Create `Operations/Transparency.cs`; Modify `Operations/DrawingOperation.cs:29`; Modify `CorelMcp.Worker.Tests/DrawingContractsTests.cs`; Extend `LiveNativeFillsTests.cs`; Modify `skills/.../references/tools.md` (:5→40, строка, JSON, jsonl), `webhost/corel_agent/policy.py:23–30`, `webhost/tests/test_transport_compatibility.py:12`, `worker/api-contract.json:3097`; regen `webhost/tests/fixtures/catalog.mjs`; Modify `webhost/tests/test_typed_policy.py`.
**Interfaces:** Produces: zod-инструмент `corel_set_transparency` {…common, static_ids, kind: none|uniform|fountain, value?: int 0..100, start?/end?: int 0..100, type?, angle_deg?, center_offset_*_percent?, applied_to: fill|outline|fill_and_outline, merge_mode whitelist, freeze?: bool} refine по kind; C# `TransparencyCommand(int[] StaticIds, string Kind, int? Value, int? Start, int? End, string? Type, double? AngleDeg, double? CenterOffsetXPercent, double? CenterOffsetYPercent, string AppliedTo, string MergeMode, bool? Freeze)`; отчёт items с наблюдаемым transparency (переиспользовать `Inspection.EffectInfo`). Цели: векторные фигуры/текст; **группа допускается только если все потомки поддержаны (иначе отказ до мутации)** — правило спека, обязательно в валидации и тестах.

- [ ] **Step 1:** Failing node tests: валидные uniform/fountain/none проходят; value 101, freeze без kind, angle при radial, неизвестный merge_mode → throw; catalog listTools == 40.
- [ ] **Step 2:** Failing worker contract tests + live test: uniform 40 на rect → EffectInfo transparency type uniform value 40; fountain linear 0→100 applied_to outline → read-back applied_to==1; none снимает; frozen lens/locked/special layer → отказ до мутации; группа с неподдержанным потомком (bitmap) → отказ до мутации, группа из поддержанных фигур → применяется ко всем потомкам; style на прозрачной цели по-прежнему отказ (барьер).
- [ ] **Step 3:** Запустить оба набора → FAIL.
- [ ] **Step 4:** Реализовать zod/DTO/Transparency.cs (`Shape.Transparency.ApplyUniformTransparency/ApplyFountainTransparency/ApplyNoTransparency`, AppliedTo/MergeMode до применения, Freeze после; проверки Frozen/locked/effects до мутации); sync-поверхности (tools.md 3 блока, policy.py, счётчики 40, catalog fixture, typed policy tests).
- [ ] **Step 5:** `npm test`, `check:contracts`, `dotnet test` non-live, live filter, `webhost` pytest non-live → PASS.
- [ ] **Step 6:** Checkpoint.

### Task 6: corel_apply_drop_shadow (id_mappings → sync 41 → снятие тени)

**Files:** Modify `src/drawing-tools.ts`; Extend `test/effects-tools.test.mjs`; Modify `Operations/DrawingContracts.cs`; Create `Operations/DropShadow.cs`; Modify `Operations/DrawingOperation.cs:29`; Extend `LiveNativeFillsTests.cs`; Modify sync-поверхности Task 5 (:5→41, policy.py, transport compat, api-contract:3097, catalog fixture, typed policy).
**Interfaces:** Produces: zod `corel_apply_drop_shadow` {…common, static_ids, kind: shadow|none, type: flat|bottom|top|left|right, opacity 0..100, feather 0..100, offset_x_mm/offset_y_mm -1000..1000, color, feather_type whitelist, feather_edge whitelist, merge_mode whitelist, fade?: int 0..100 (запрещён при flat)}; C# `DropShadowCommand(...)`; отчёт `id_mappings:[{old_static_id,new_static_id}]` + created/deleted (образец convert); при kind none — typed-путь снятия, найденный live-проверкой (`IVGEffect.Delete` или эквивалент); если пути нет — схема запрещает kind none и limitation документируется (Task 8).

- [ ] **Step 1:** Failing tests: node (валидный shadow; fade при flat → throw; группа/bitmap в static_ids не различимы схемой → worker-отказ); worker live: shadow на rect → старый ID исчезает, новый group ID в id_mappings, потомки/геометрия сохранены, EffectInfo содержит drop shadow; повторная тень → отказ до мутации; группа → отказ; locked → отказ; none снимает тень и возвращает исходный тип фигуры (если typed-путь найден).
- [ ] **Step 2:** Наборы → FAIL.
- [ ] **Step 3:** Реализовать DTO/DropShadow.cs (`Shape.CreateDropShadow(...)`; observe новых ID через `target.Observe`/TreeIds-эквивалент существующего convert-кода; refusal предков/потомков с effects); sync 41.
- [ ] **Step 4:** Все наборы зелёные; зафиксировать в плане найденный путь снятия тени (или его отсутствие).
- [ ] **Step 5:** Checkpoint.

### Task 7: api-contract ledger + контракты

**Files:** Modify `worker/api-contract.json` (signatures IVGFill.ApplyFountainFill, IVGFountainFill.*, IVGFountainColors.Add/Count/Item, IVGTransparency.Apply*, IVGShape.CreateDropShadow; enums cdrFountainFillType/SpreadMethod/TransparencyAppliedTo/DropShadowType/FeatherType/MergeMode whitelist; live_status из Tasks 4–6; catalog_tools уже 41).
**Interfaces:** Consumes: live-результаты Tasks 4–6; typelib sha не меняется.

- [ ] **Step 1:** Failing: `test/worker-package.test.mjs` + `scripts/worker-package.mjs` verify (typelib binding) и новая node-проверка полноты ledger (если существует; иначе assert вручную через describe_api-сверку скриптом `scripts/check-contracts.mjs` не читает ledger → добавить node test `test/api-contract-ledger.test.mjs`, сверяющий новые записи с `corel_describe_api`-дампом, сохранённым в fixtures).
- [ ] **Step 2:** FAIL → заполнить ledger → PASS; `npm run check:contracts`, `dotnet test` non-live → PASS.
- [ ] **Step 3:** Checkpoint.

### Task 8: Скилл/рецепты/доки + limitation-записи

**Files:** Create `skills/coreldraw-automation/examples/fountain-fill.json`, `examples/transparency-shadow.json`; Modify `references/recipes.md:12`, `references/operations.md:9,21,23–25`, `references/pitfalls.md:10–13`, `references/api-v27.md`, `SKILL.md:20,35`, `docs/architecture.md:54–66,87–89`, `worker/README.md:60–67,96–100` (+ примечание про prerequisite `build-worker.ps1 -GenerateInteropOnly`), `scripts/check-contracts.mjs` (хардкод-список рецептов += новые examples).
**Interfaces:** Consumes: финальные схемы Tasks 2–6; результат снятия тени Task 6.

- [ ] **Step 1:** Failing: `test/recipe-contract.test.mjs` (новые examples не существуют/не парсятся) и `test/documentation-contracts.test.mjs`.
- [ ] **Step 2:** Написать рецепты (fountain multi-stop; transparency uniform+fountain; shadow с id_mappings; если kind none недоступен — рецепт без него + limitation в pitfalls/operations/architecture: группы под тенью, затенённые фигуры вне allowTransparency, mesh non-goal).
- [ ] **Step 3:** `npm test`, `check:contracts` → PASS.
- [ ] **Step 4:** Checkpoint.

### Task 9: Полные gates + нативная перекраска геймпада

**Files:** artifacts (вне repo-кода): `artifacts/gamepad-logitech-native.cdr/.png`; план-отчёт.
**Interfaces:** Consumes: все операции Tasks 2–6 через MCP текущего worktree-сервера.

- [ ] **Step 1:** `powershell -NoProfile -File scripts/Verify-CorelAgent.ps1 -SourceOnly -DotnetPath '<dotnet 10.0.404>'` → VERIFY_EXIT_CODE=0.
- [ ] **Step 2:** Live suites последовательно: `$env:CORELDRAW_WORKER_LIVE='1'; npm run test:integration` (+ hybrid integration отдельно) → PASS; mutex test не параллелить.
- [ ] **Step 3:** Перекраска: новый документ 240×150; геометрия корпуса/грипов/кнопок через create_shapes/create_curve (как в плоской версии); заливки — fountain (корпус linear multi-stop, кнопки radial + spec-стопы, стики radial), прозрачность — fountain для мягких теней/углублений, drop shadow под корпусом; текстура стиков — мозаика uniform (существующий путь); preview и визуальная сверка с фото; сохранить `gamepad-logitech-native.cdr`, экспорт PNG 300 dpi.
- [ ] **Step 4:** Отчёт пользователю: числа тестов, gates, пути артефактов, список limitation; спросить отдельное разрешение на коммиты/установку.
- [ ] **Step 5:** Checkpoint финальный.
