# Дизайн: нативные заливки и эффекты CorelDRAW в corel-mcp (fountain / transparency / drop shadow)

Дата: 2026-11-16 · Статус: черновик до builder-check и утверждения · Ветка: 14-11-2026

## Intent и why

Пользователь воспроизводит фотографию геймпада Logitech векторными средствами CorelDRAW 27 через MCP.
Текущий контракт сервера умеет только uniform-заливку/обводку, поэтому градиенты пришлось получать
импортом SVG. Цель: дать серверу **нативные** типизированные операции Corel — многостоповые fountain-заливки,
uniform/fountain прозрачность и drop shadow — чтобы фото-подобная раскраска создавалась средствами самого
CorelDRAW и оставалась редактируемой в UI, без SVG-костылей и без произвольного кода.

## Goals

- `corel_style_shapes`: аддитивный kind `fountain` в союзе fill (линейный/радиальный/конический/квадратный,
  многостоповые цвета с позициями, angle, center offset, spread, steps/edge pad/mid point, smooth blend).
- Новый `corel_set_transparency`: uniform (0–100) и fountain (start/end/type/angle/center), `applied_to`
  (fill/outline/fill+outline), merge mode; снятие через kind `none` (`ApplyNoTransparency`).
- Новый `corel_apply_drop_shadow`: flat/bottom/top/left/right, opacity, feather, offsets, color, feather type,
  feather edge, merge mode; снятие тени отдельным kind `none` (семантика удаления эффекта — см. ниже).
- Возврат фактических StaticID и `id_mappings` там, где ID меняется (drop shadow оборачивает фигуру в
  `cdrDropShadowGroupShape`), по образцу `corel_convert_to_curves`.
- Полная валидация целей/значений до первой мутации, включая BeginCommandGroup; отчёты job с
  affected/created/deleted, cleanup, warnings — как у существующих операций.
- Синхронность: MCP-схемы, worker DTO, api-contract.json, скилл, references/tools.md + operations.md + recipes.md,
  host capability table, docs, installer probes, тесты.

## Non-goals / anti-goals

- **Mesh fill — non-goal с доказательством**: в установленной type library VGCore 27.2 (`describe_api`)
  нет интерфейсов Mesh (`types filter=Mesh` пусто) и нет методов создания/правки меша у `Shape`/`Fill`;
  существует только enum `cdrMeshFillShape=20`. Типизированный dispatch-вызов невозможен; произвольный
  COM/Invoke запрещён правилами проекта. Аппроксимация меша — наслоенные fountain-прозрачности и мозаика
  uniform-фигур (уже используется в артефакте геймпада).
- Не трогаем hybrid-пакеты (`corel_create_batch`/`corel_update_batch`): остаются uniform-only барьером;
  новые kind там отклоняются до мутации. VBA runtime не расширяем.
- Не добавляем pattern/texture заливки, inner shadow, lens-эффекты кроме прозрачности, blend/extrude/bevel.
- Не меняем исторические DTO/отчёты; союз fill расширяется аддитивно, старые запросы байт-совместимы.
- Anti-goal: «универсальный appearance-инструмент» с произвольными полями; каждая операция — фиксированный
  проверенный набор параметров со строгой схемой.
- Anti-goal: автоматический replay/undo после timed_out/unknown/partial — сохраняется существующая семантика.

## Constraints (правила проекта)

- Один STA worker, мьютекс `Local\CorelDrawMcp_v27`; polling не вызывает COM; deadline не отменяет работу.
- Никаких логов в stdout сервера; аргументы не через shell/Invoke-Expression; без Roslyn/универсального COM Invoke/fallback.
- Проверка всех входных целей/значений до первой мутации; locked/special layer, bitmap, unsupported effects —
  отказ до мутации (для transparency/shadow цели — векторные фигуры без live effects/PowerClip; bitmap запрещён).
- Точные document_id/page_index/layer_id/StaticID; без выбора по имени/индексу; без Application.Quit.
- Подписи сверяются с Context7 `/websites/community_coreldraw_sdk_api_draw`, API 27, установленной type library
  и live tests; фиксируются в worker/api-contract.json.
- Коммиты и установка в пользовательское окружение — только по отдельному разрешению.

## Approaches

1. **Выбран пользователем**: расширить `corel_style_shapes` союзом fill `fountain` + отдельные
   `corel_set_transparency` и `corel_apply_drop_shadow`. Одна точка входа для заливок; эффекты отдельно,
   т.к. у них своя семантика ID и cleanup. Аддитивно и обратно совместимо.
2. Отклонён: отдельный `corel_apply_fountain_fill` при нетронутом style_shapes — целость исторических схем,
   но две точки входа для заливок и расхождение скилла/рецептов.
3. Отклонён: мега-инструмент `corel_apply_appearance` — меньше job-ов, но огромная поверхность валидации и
   неоднозначная семантика частичного отказа (тень меняет ID, прозрачность — нет).

## Проверенный контракт API 27 (describe_api, установленная type library)

- `Fill.ApplyFountainFill(StartColor:IVGColor, EndColor:IVGColor, Type:cdrFountainFillType, Angle:R8,
  Steps:I4, EdgePad:I4, MidPoint:I4, BlendType:cdrFountainFillBlendType, CenterOffsetX:R8, CenterOffsetY:R8) -> IVGFountainFill`
- `IVGFountainFill`: `Type`, `StartX/Y`, `EndX/Y`, `Angle/SetAngle`, `EdgePad/SetEdgePad`, `Steps`, `BlendType`,
  `MidPoint`, `Colors -> IVGFountainColors`, `StartColor/EndColor`, `CenterOffsetX/Y`, `SmoothBlend`,
  `SpreadMethod`, `ScaleX/Y`, `Skew`, `MergeMode`, `MakeOpaque`, `SetTransformations`.
- `IVGFountainColors`: `Count`, `Item(Index)`, `Add(Color:IVGColor, Position:I4)`, `First/Last`.
  Позиции стопов — целые 0..100 (проверить live: диапазон и сортировку; зафиксировать в api-contract).
- `cdrFountainFillType`: linear=1, radial=2, conical=3, square=4.
- `cdrFountainFillSpreadMethod`: pad=0, reflect=1, repeat=2.
- `Shape.Transparency -> IVGTransparency`: `ApplyUniformTransparency(Value:I4)`,
  `ApplyFountainTransparency(Start:I4, End:I4, Type, Angle, Steps, EdgePad, MidPoint, CenterOffsetX, CenterOffsetY) -> IVGFountainFill`,
  `ApplyNoTransparency()`, `AppliedTo` (fill=0, outline=1, fill+outline=2), `MergeMode`, `Freeze/Unfreeze`, `Frozen`.
- `Shape.CreateDropShadow(Type:cdrDropShadowType, Opacity:I4, Feather:I4, OffsetX:R8, OffsetY:R8, Color:IVGColor,
  FeatherType:cdrFeatherType, FeatherEdge:cdrEdgeType, PerspectiveAngle:R8, PerspectiveStretch:R8, Fade:I4,
  MergeMode:cdrMergeMode) -> IVGEffect`.
- `cdrDropShadowType`: flat=0, bottom=1, top=2, left=3, right=4.
- `cdrFeatherType`: inside=0, middle=1, outside=2, average=3, gaussian=4.
- `cdrMergeMode`: normal=0, multiply=9, screen=28, overlay=29, … (полный список в тип-либе; схема ограничит
  whitelisted-набором: normal, multiply, screen, overlay, behind=27).
- Mesh: отсутствует (см. non-goals).

## Семантика новых операций

### fill kind `fountain` (в corel_style_shapes)

Поля: `type` (linear/radial/conical/square), `stops` (2..16: {position, color rgb|cmyk}; крайние стопы
фиксированы 0 и 100, промежуточные 1..99 строго возрастают и уникальны — проверка до мутации),
`angle_deg` (-360..360; применим linear/conical/square, для radial отклоняется схемой),
`center_offset_x_percent` / `center_offset_y_percent` (-100..100; **проценты** ширины/высоты объекта по API 27,
передаются как есть, без конвертации единиц; применимы radial/square/conical),
`spread` (pad/reflect/repeat), `steps` (2..999), `edge_pad` (0..100), `mid_point` (0..100),
`smooth_blend` bool, `blend_type` (whitelist enum из typelib). Применение: `ApplyFountainFill` первым/последним
стопом, затем `Colors.Add` для промежуточных стопов в порядке позиций. Outline в том же вызове сохраняется
как сейчас. Отсутствующие свойства не меняются.

### corel_set_transparency

Параметры: request_id, document_id, page_index, static_ids (1..100), kind none|uniform|fountain,
uniform value 0..100; fountain start/end 0..100 + type/angle_deg (linear/conical/square) +
`center_offset_x_percent`/`center_offset_y_percent` (-100..100, проценты, без конвертации); applied_to;
merge_mode whitelist; freeze bool (только после применения; Frozen проверяется до мутации). Цели: векторные
фигуры/текст без bitmap/live effects/PowerClip; группа допускается только если все потомки поддержаны
(иначе отказ до мутации). Повторное применение/снятие — через kind союза, без отдельных команд.
ID не меняются; отчёт содержит фактический transparency type/value после применения (наблюдаемое чтение).

### corel_apply_drop_shadow

Параметры: request_id, document_id, page_index, static_ids (1..100), kind shadow|none;
type, opacity 0..100, feather 0..100, offset_x/y_mm (-1000..1000), color, feather_type, feather_edge,
merge_mode whitelist, fade 0..100 (только perspective-типы? flat игнорирует — схема запрещает fade для flat).
Применение создаёт effect-группу: **StaticID цели меняется** → отчёт обязан вернуть `id_mappings`
(old→new) и created/deleted ids, по образцу convert. kind `none`: удаление тени — проверить live доступный
dispatch-путь (`IVGEffect.Delete`/разгруппировка с сохранением фигуры); если надёжного typed-пути снятия нет —
kind `none` отклоняется схемой на этапе реализации и документируется как limitation (не fallback).
Цели (решение пользователя после builder-check): **одиночные векторные фигуры (types 1/2/3/4) и текст (6)**;
группы — documented limitation v1 (без id_mappings по поддереву). Без существующих effects/PowerClip/bitmap;
повторное применение тени к уже затенённой цели — отказ до мутации (без двойного эффекта).

### Validation / cleanup / jobs

- Все проверки до BeginCommandGroup: существование целей, слой не special/locked, тип поддержан,
  диапазоны чисел, whitelists enum, цвета BMP-безопасны (цвет — числа), stops отсортированы/уникальны.
- Cleanup восстанавливает выделение/активную страницу как сейчас; при ошибке после начала мутации —
  partial:true с наблюдаемыми ID, без replay/undo.
- Deadline/таймауты — существующая семантика; новые инструменты участвуют в том же mutex/STA.

## Точки вставки в репозиторий (карта исследователя, ветка 14-11-2026)

### Схемы и регистрация (TS)
- `src/drawing-tools.ts:30` — союз fill (`none|uniform`): добавить `fountain` **только здесь**;
  `:32` drawingFields общий с batch → **batch получает собственный союз `batchFill = none|uniform`**
  в `src/batch-tools.ts:7` (иначе новый kind молча расширит hybrid-схемы).
- `src/drawing-tools.ts:140–141` — corel_style_shapes (refine fill||outline сохраняется).
- Новые определения `corel_set_transparency`, `corel_apply_drop_shadow` — в `drawingToolDefinitions`
  (`src/drawing-tools.ts:120–151`), регистрация автоматом через `src/server.ts:68–97`.
- Счётчик инструментов 39→41 синхронно: `skills/.../references/tools.md:5`,
  `webhost/tests/test_transport_compatibility.py:12`, `worker/api-contract.json:3097`.

### Worker DTO / dispatcher / COM
- `Operations/DrawingContracts.cs:31–35` — FillSpec: добавить `[JsonDerivedType(typeof(FountainFill),"fountain")]`;
  `:74` IsDrawingTool += два новых имени; `:85–94` switch += чтения новых команд; `:158–162` валидация Style
  += проверка stops (2..16, позиции 0..100 строго возрастают/уникальны), диапазоны angle/offsets/steps/edge/mid.
- Новые записи команд: `TransparencyCommand`, `DropShadowCommand` — по образцу `AssemblyContracts.cs:14–18,34–35`
  (ClipCommand), валидация до мутации там же.
- `Operations/DrawingOperation.cs:29` — switch += `TransparencyCommand => Transparency.Prepare`,
  `DropShadowCommand => DropShadow.Prepare`; новые файлы `Operations/Transparency.cs`, `Operations/DropShadow.cs`
  по образцу `Operations/PowerClip.cs` (resolve/validate → c.Mutating() → apply → RecordMutation → Stage).
- `Operations/Styles.cs:15–19` — ветка fountain: `ApplyFountainFill(first,last,type,angle,steps,edgePad,midPoint,
  blendType,centerX,centerY)` затем `Colors.Add(color,position)` для промежуточных стопов; цвета создавать
  до мутационного замыкания (образец L31–36).
- **Барьер batch**: `Hybrid/NativeBatchContracts.cs:36–41` (Style re-parse через corel_style_shapes) —
  явный отказ `fountain` до мутации ( BATCH_FILL_UNSUPPORTED ); row codec/VBA/manifest не трогаем.
- **Read-back**: `Operations/DrawingOperation.cs:146–161` Info и `Operations/Inspection.cs:68–72` —
  расширить fountain (type/angle/stops/positions/center/spread), чтобы inspect и postcheck видели градиент;
  `Inspection.cs:93–126` EffectInfo уже читает transparency — переиспользовать для отчёта set_transparency.
- **Сохраняемые барьеры**: `DrawingOperation.cs:56–59` allowTransparency whitelist и `:115–125` effects gate
  (единственный cdrLens unfrozen uniform/fountain) не меняем; style/convert/boolean/scale/layout/update_batch
  на прозрачной цели остаются запрещены (LiveEffectsTests.cs:228–255). Затенённая фигура (effect-группа)
  не входит в allowTransparency → мутации кроме чтения/group отклоняются существующим gate; документируем как
  limitation, whitelist не расширяем без отдельного решения.
- `GeometrySnapshot.cs:100,103` (measure отвергает effects/transparency) — не меняем.

### Контракты и проверка
- `worker/api-contract.json`: добавить signatures IVGFill.ApplyFountainFill, IVGFountainFill (Type/Angle/
  CenterOffset*/SpreadMethod/SmoothBlend/Colors), IVGFountainColors.Add/Count/Item, IVGTransparency.
  ApplyUniformTransparency/ApplyFountainTransparency/ApplyNoTransparency/AppliedTo/MergeMode/Freeze,
  IVGShape.CreateDropShadow + enum-записи; live_status заполняется live-gate; typelib sha не меняется.
- `scripts/check-contracts.mjs` сверяет dist-zod ↔ tools.md (три блока) и рецепты → новые строки/описания/jsonl
  в `references/tools.md:19–57,132–172,180–220` + новый рецепт `examples/fountain-fill.json` и строка в
  `references/recipes.md:12`; transparency/shadow рецепты по желанию билдера (минимум один каждый).
- Предыдущее свидетельство: `docs/live-backend-comparison-2026-11-12.md:37–45,204–213` — fountain/pattern/
  texture/fountain-transparency уже проверялись прототипами; mesh не верифицирован (совпадает с non-goal).

### Тесты
- Node: `test/drawing-tools.test.mjs` (style-кейсы L48–50 += fountain валид/невалид), новый
  `test/effects-tools.test.mjs` (схемы transparency/shadow, отказы до executor), `test/drawing-catalog.test.mjs`
  (listTools 41), `test/batch-tools.test.mjs` (fountain в batch отклоняется схемой).
- Worker non-live: `DrawingContractsTests.cs` += fountain/stops/новые команды; `NativeBatchTests.cs` += отказ fountain.
- Worker live (CORELDRAW_WORKER_LIVE=1, только свои документы): новый `LiveNativeFillsTests.cs` по образцу
  `LiveEffectsTests.cs`: multi-stop fountain на rect/ellipse/curve + read-back; transparency uniform/fountain/
  applied_to + снятие; drop shadow + id_mappings + наблюдаемый effect; отказы (bitmap/locked/повторная тень/
  несортированные stops/прозрачная цель под style); cleanup.completed=true; закрытие только своих документов.
  Test-only COM adapter (`LiveDrawingTests.cs:185–222`) не расширяем — новые операции идут через штатный клиент.
- Integration: `test/integration-drawing.mjs` += fountain-кейс; `test/integration-hybrid.mjs` += отказ fountain в batch.
- Host: `webhost/corel_agent/policy.py:23–30` DRAWING_MUTATIONS += два имени (POSTCHECKS inspect+preview автоматом);
  `webhost/tests/fixtures/catalog.mjs` перегенерируется из dist; `test_typed_policy.py` обновить.
- Полный gate: `Verify-CorelAgent.ps1 -SourceOnly` + live suites последовательно (mutex test не параллелить).

### Документация/скилл
- `SKILL.md:20,35`, `references/operations.md:9,21,23–25`, `references/pitfalls.md:10–13`,
  `docs/architecture.md:54–66,87–89`, `worker/README.md:60–67,96–100`, `references/api-v27.md` —
  синхронно в том же изменении; historical reports/DTO не переписываем.

## Тесты и gates

- Node unit: схемы fountain/transparency/shadow (валидные/невалидные payloads, отказы до мутации),
  сериализация DTO, id_mappings в отчётах.
- Worker unit (dotnet test): DTO-валидация, mapping enum, построение COM-вызовов на моках/контрактах.
- check:contracts: новые записи api-contract.json сверяются с установленной type library.
- Live gate (CORELDRAW_WORKER_LIVE=1, только собственные временные документы): fountain multi-stop на
  прямоугольнике/эллипсе/кривой; transparency uniform+fountain и applied_to; drop shadow с проверкой
  id_mappings и наблюдаемого типа эффекта; отказные кейсы (bitmap, locked, повторная тень, несортированные stops);
  cleanup.completed=true; закрытие только своих документов.
- Визуальная приёмка: перекраска геймпада новыми операциями (замена SVG-импорта), preview до/после.
- Verify-CorelAgent.ps1 -SourceOnly целиком зелёный до передачи пользователю.

## Rollout

- Без коммитов и без установки в пользовательское окружение без отдельного разрешения.
- Готовый distributable пакет остаётся заблокированным interop-gate; изменения только в исходниках.
- Документация/скилл/capability table обновляются в том же изменении; старые отчёты не переписываются.

## Решения после builder-check

- **Цели тени** (вопрос пользователя): одиночные векторные фигуры + текст; группы — limitation v1.
  Ответ пользователя зафиксирован в семантике выше.
- **Единицы центра fountain** (моё решение, форсировано API 27): `CenterOffsetX/Y` — проценты ширины/высоты
  объекта; поля названы `center_offset_x_percent`/`center_offset_y_percent` (-100..100), передаются как есть
  без `target.U()`-конвертации; одинаково для заливки и fountain-прозрачности. Суффикс `_mm` из черновика
  удалён как ошибочный.
- **Angle и square** (моё решение по SDK): `angle_deg` действует для linear/conical/square; для radial
  отклоняется схемой до мутации.
- **Якорение стопов** (моё решение, форсировано API): крайние стопы 0/100 фиксированы схемой, промежуточные
  1..99 строго возрастают/уникальны; live-проверка диапазона остаётся в gates.
- **Повторная прозрачность** (форсировано союзом kind): применение/снятие через kind none|uniform|fountain,
  отдельных команд нет; валидация по образцу PowerClip.cs без изменения allowTransparency.
- **Hybrid и fountain** (форсировано row codec): fountain остаётся на C#-пути; барьер в batch ставится
  до маршрутизации (схема batchFill + отказ в NativeBatchContracts.Style).
- **Whitelists blend_type/feather_edge/merge_mode** (моё решение): перечисляются из установленной typelib
  на этапе реализации и фиксируются в api-contract.json; схема принимает только whitelisted-имена.
- **fade/perspective и снятие тени** (делегировано билдеру): дефолты perspective-полей и наличие typed-пути
  снятия тени определяются live-проверкой; при отсутствии надёжного пути kind `none` для тени отклоняется
  схемой и документируется как limitation, без fallback.

## Оставлено билдеру

- Точные имена файлов/строк вставки (из карты), имена DTO-записей в стиле существующих.
- Live-проверка диапазона позиций стопов и пути снятия drop shadow; фиксация в api-contract.json.
- Формулировки сообщений об ошибках в стиле существующих кодов (VALIDATION_*, UNSUPPORTED_*).
