# Проверка исходников Corel MCP 0.3.0 — 12 ноября 2026

Статус: техническая реализация исходников проверена; распространяемый релиз **не готов**. Права на generated VGCore interop не подтверждены, реальный self-contained publish и чистая установка не выполнялись. Локальная сборка не заменяет выбранный пользователем распространяемый пакет. Этапы9/10 плана остаются незавершёнными в части поставки.

## Что изменено

C# STA worker для .NET10/Windows x64 — единственный backend; каталог37 инструментов. Произвольные execute/build VBA удалены из регистрации, runtime, prompts и примеров. Исторические DTO/ошибки/retained-GMS evidence читаются, старые операции не запускаются. Один mutex, STA pumping, проверка IDs/revisions, request_id, частичные и неизвестные исходы сохранены.

Импорт использует замороженный локальный файл и собственный CDR с provenance markers. Bitmap и смешанные группы поддерживаются transform/duplicate/group/layer/align/grid/layout; boolean/convert/style сохраняют векторные ограничения. Шрифты/эффекты диагностируются как наблюдаемые данные, source/font/effect fidelity остаются unknown. Availability по Windows не доказывает отсутствие подмены Corel.

Добавлены read-only admission старых owner/reports/SQLite, проверка комплекта/ready до установки, publisher с первым licensing gate и manifest всех файлов. Новые unknown/частичные failed отчёты не проходят admission. Старые файлы не переписываются. Установленный сервер и чужой дизайн webui сохранены.

## Проверки

| Проверка | Результат |
| --- | --- |
| Полный C# с live | 160 passed, 0 failed, 0 skipped |
| Node/MCP | 141 passed, 0 failed, 1 opt-in skipped |
| Host non-live | 395 passed, 2 skipped, 1 opt-in live deselected |
| Browser | 70 passed |
| Contracts, RUFF, OpenAPI, API codegen, UI build | passed |
| Реальные integration suites | 5 passed |

Общий SourceOnly Verify завершён с exit0. Режим исходников не подтверждает готовый пакет.

Команда общей проверки: scripts/Verify-CorelAgent.ps1 -SourceOnly -DotnetPath <изолированный SDK10.0.404>. SDK/runtime в пределах рабочего дерева; системная установка и PATH не менялись. Итоговый журнал: .superpowers/sdd/1794339253999-csharp-worker-typed-corel-tools/final-fix-suite-acceptance.log.

Пять реальных integration suites пройдены после fix pass:

- artifacts/integration/8b77fd5a-63bf-4101-ab3b-dfec453575b6/verification.json
- artifacts/integration-drawing/2607df0b-025d-41f7-ba12-17517499cb1b/verification.json
- artifacts/integration-production/0b54f970-8d24-47d6-a1ef-63a75823b11f/verification.json
- artifacts/integration-layout/340c7b20-90d2-4e10-9cf0-04f12e14a4f7/verification.json
- artifacts/import/482df5aa-24f4-440e-ba84-f98401c6419a/verification.json

Отдельный HTTP/Runtime/Corel panel smoke пройден ранее на0.3.0: C:/Users/olegiy/AppData/Local/CorelMcp_Smoke_17c537d8/CorelMcp-panel-smoke-c5fd16ef-a958-4876-b884-01f3a0ca2046/verification.json. Обычный Verify не считается заменой этому opt-in gate.

Статический audit112 runtime/source files: запрещённых VBA/динамической компиляции/shell:true вызовов и исполняемых .bas/.gms нет. Historical readers содержат только прежние поля/коды; PDFVBASettings — native PDF settings interface. Build-time interop generator и frozen benchmark baseline не входят в runtime. Лог: .superpowers/sdd/1794339253999-csharp-worker-typed-corel-tools/stage10-static.json.

C# live-tests и integrations создавали только собственные документы; контрольные документы — также тестовые. Последняя inventory complete/idle: документов0, temporary GMS0, VBE CorelMcp_*0. Corel Quit/Kill не вызывались. Реальных пользовательских drawing во время проверок не было; сохранность непустого пользовательского документа не заявляется.

## Исправления итогового review

Один fresh-context gpt-6-astra whole-branch review: Critical0, Important3, Minor0. Все три Important воспроизведены RED и исправлены; повторный reviewer не запускался. Материалы: .superpowers/sdd/1794339253999-csharp-worker-typed-corel-tools/final-review.md и review-working-tree.diff.

1. Bitmap assembly: живой mm/inch сценарий import→transform/scale→duplicate→mixed group→layer→grid→layout; stale revision и vector-only отказы проверены.
2. Admission failed: missing/null/true applied и partial блокируются, proven false допускается. GMS absence proof не доказывает исход мутации. JSON и SQLite остаются неизменными.
3. Import diagnostics: наблюдаемые шрифты/эффекты и явные unknown/warnings. Новый тест обнаружил native CrlFOM.dll crash при позднем освобождении RCW частного текстового документа. Все private COM references вынесены в отдельный scope, освобождаются при живом private document до его закрытия. Исходный SVG text/effect fixture повторён трижды; диагностика отказов сохранена, unknown задания не переигрывались. Гипотеза о виновности font catalog не подтвердилась.

## Производительность

Corel27.2.0.135 уже работал; cold означает новый MCP/worker/status. Node24.18.1, C#FDD .NET10.0.15. Три cold samples, по одной последовательной выборке остальных операций. Baseline28b6cb4 запускался отдельно, установленный owner не использовался.

| Операция | Прежняя версия, с | C#0.3, с |
| --- | ---: | ---: |
| Cold status, медиана | 1.462 | 0.586 |
| Создать1000 прямоугольников | 1.200 | 35.537 |
| Прочитать1000 фигур | 23.646 | 12.123 |
| Измерить100 фигур | 13.842 | 38.330 |
| PNG export | 0.797 | 0.169 |

Прежнее создание — один фиксированный VBA loop; новое —10 batch по100 через согласованный публичный лимит. Чтение —2page по500. В обеих версиях min_width=3.0762mm с native defaultoutline. Создание и измерение в C# здесь медленнее; общего ускорения не заявляется. Raw: [performance.json](../artifacts/performance/ffa061e1/performance.json).

## Незавершённые условия поставки

- Применимое подтверждение Corel на распространение generated VGCore.Interop.dll отсутствует. [Официальные источники и неотправленный запрос](../worker/INTEROP-DISTRIBUTION.md). Официальный канал: [Permissions Department](https://www.corel.com/en/permissions/), permissions@corel.com.
- Publisher действительно остановлен INTEROP_RIGHTS_UNCONFIRMED до generator/SDK/output. Синтетические license/manifest fixtures не являются разрешением или опубликованным пакетом.
- Self-contained publish, чистая установка без глобального SDK/runtime и её приёмка не выполнены.
- Текущее окружение отдельно отклоняет активацию с OWNER_NOT_STOPPED. Существующий owner не останавливался; нужен отдельный разрешённый переход со старой версии после разрешения её незавершённых исходов.
- Коммиты, регистрация/установка в user environment, публикация и отправка внешнего письма не выполнялись. Workspace/диагностика сохранены.

## Все решения исполнителя

Ниже полный список Ruling из журнала, в исходном порядке, включая цену/риск каждого решения.

- Ruling: скрипт task-brief распознаёт только английский Task, план использует Этап — извлекать этапы по заголовкам PowerShell, сохраняя исходный план — риск ошибочного диапазона устраняется чтением полученного brief.

- Ruling: сборочные артефакты и SDK хранить в каталоге этого плана, не изменять PATH или системный dotnet — локальная установка явно разрешена пользователем — сборочные команды должны передавать точный путь dotnet.

- Ruling: Tlbimp.exe отсутствует; генерировать сборку из зарегистрированной VGCore через тот же штатный TypeLibConverter в Windows PowerShell 5.1 (только build-time). Проверять GUID/версию/SYS_WIN64 и fingerprint, не использовать поставляемую Corel DLL как подмену — риск несовместимости проверяется net10/live gate. Скрипты PowerShell 5.1 сохраняются UTF-8 BOM для русских строк.

- Ruling: filter-specific свойства отсутствуют в VGCoreAuto.tlb. Они находятся в установленных FilterAI/FilterEPS/FilterHPGL/FilterJPG.tlb (1.0, SYS_WIN64). Использовать узкие статические [ComImport]/IDispatch интерфейсы с проверенными IID/DispId/типами; нет dynamic/Invoke по имени и нет runtime compilation. Ошибочная декларация должна выявиться обязательным live export gate. Проверено: IAIExport daedea01-0cb9-11d4-b6b4-009027575a12, TextAsCurves dispid11 VT_BOOL; IEPSExport daede301-0cb9-11d4-b6b4-009027575a12, dispid14 VT_BOOL; IHPGLExport daedec01-0cb9-11d4-b6b4-009027575a12, PlotterUnits dispid24 VT_I2 (short); IJPGExport daede201-0cb9-11d4-b6b4-009027575a12, Compression dispid13 VT_I4. Все dual/dispatchable. SHA-256 были прочитаны и должны войти в api-contract.json.

- Ruling: COM IUnknown страниц/слоёв нестабилен между чтениями. Использовать документированный SDK 27 TreeNode.Handle как внутренний native key, наружу только session-scoped opaque IDs; документ остаётся привязан по COM identity. Live proof: одинаковые имена двух слоёв имеют разные ID, page ID сохраняется после reorder, удалённая и заменённая страница даёт STALE_PAGE без fallback.

- Ruling: Corel ExportFilter wrapper не поддерживает QI filter-specific IID, хотя GetTypeInfo возвращает этот интерфейс. Для четырёх фиксированных свойств использовать narrow [ComImport] default IDispatch (IID00020400), статические DispId и предварительную проверку runtime ITypeInfo GUID. Это не универсальный Invoke: нет пользовательского имени/выражения/метода и нет IDispatch.Invoke API в коде. Live all-format export подтверждает совместимость. Прежние попытки и ошибки сохранены как диагностика.

- Ruling: все новые координатные поля используют суффикс _mm (x_mm/y_mm/anchor_x_mm и т.д.), font_size_pt/angle_deg явно определяют единицы — спецификация задаёт мм, но не точные имена всех полей — цена ошибки ограничена новым ещё не поставляемым интерфейсом.

- Ruling: группа и её потомок не принимаются вместе в новых мутациях (OVERLAPPING_TARGETS), чтобы избежать двойного преобразования/копирования — прежний measure сохраняет одновременную группу/child — тест проверяет отказ без изменения документа.

- Ruling: legacy curve-node и text-edit рецепты окончательно покрываются в этапе 5, где соответствующие инструменты определены самим планом; stage4 переносит их создание/конверсию и прочие базовые операции, не реализуя преждевременно вторую версию API.

- Ruling: CRLF нормализуется CorelDRAW в CR, что проверено live и возвращается как фактический текст. Это штатная модель абзацев Corel, не silent loss Unicode; ordinal literal search работает по фактическому Story.WideText.

- Ruling: DisplayCurve у native text возвращает null (live RED), хотя parametric rectangle/ellipse поддержаны. Для text glyph revision использовать прямой typed ConvertToCurves только в собственной GeometryIsolation.Clone, после проверки StaticID/type/физической геометрии. Исходный текст не конвертируется. Private generated glyph IDs не входят в hash, иначе dry_run/apply могли бы расходиться. Источник Origin/Unit перенесён в private doc; close выполняется в finally, непризнанный cleanup блокирует apply.

- Ruling: BeginCommandGroup сам меняет Dirty у сохранённого документа — помечать начало мутации до вызова Begin и подтверждать наблюдаемое изменение Dirty внутри body после успешного открытия группы. Ошибка до создания фигуры тогда applied:true с пустыми shape IDs; Dirty не сбрасывается и undo/replay не выполняются. Цена ошибки: консервативный unknown при неподтверждённом Begin, вместо ложного applied:false.

- Ruling: native import отклонил frozen path длиной 263 символа HRESULT E_FAIL, тот же MCP сценарий с коротким путём прошёл. Пути native input/selected PDF/transfer CDR ограничить 259 UTF-16 до создания private doc; NATIVE_PATH_TOO_LONG/applied:false, никакого обхода через чужие каталоги. Цена ошибки: отказ на длинных путях, которые отдельный фильтр мог бы поддержать. Unit RED→GREEN и успешный короткий live recipe сохранены.

- Ruling: прежний mcp.guide содержал активные VBA-инструкции и утекал в system prompt после удаления tool. Runtime получает статические инструкции typed-операций и фактические MCP JSON Schemas, без legacy guide append; RED->GREEN prompt test. Цена ошибки: до обновления guides в этапе 9 подробности доступны через каталог/описания, не через старый текстовый guide.

- Ruling: panel smoke перенесён на schema_version2 с hash готовой JSON geometry-операции вместо VBA sha. Публичный каталог не содержит изменения selection, а StateScope восстанавливает исходное пустое выделение. Smoke проверяет negative EMPTY_SELECTION, два page preview и сохранение пустого selection; positive selection preview остаётся native fixture-тестом, без скрытого COM/VBA setup. Цена ошибки: UI smoke не создаёт новое выделение для положительного selection preview. 19/19 unit fixtures passed; настоящий opt-in live smoke ещё должен пройти этап10.

- Ruling: review-package script отклонил пустой commit range28b6cb4..HEAD, потому что коммиты не разрешены. Подготовлен review-working-tree.diff + SHA256 manifest всех tracked/untracked файлов относительно baseline; reviewer читает реальную рабочую копию и diff, не пустой commit range. Цена ошибки: preexisting UI изменения видны в diff, поэтому они явно отделены в задании reviewer и не откатываются.

- Ruling: единственный whole-branch reviewer запускается после независимой реализации, пока завершается финальная проверка, при остающемся внешнем licensing gate — не откладывать полезный review до неопределённого подтверждения лицензии. Цена ошибки: verdict не заменяет publish/clean-install acceptance; любые непроверенные gates остаются явно незавершёнными.

- Ruling: performance сравнивается с frozen baseline28b6cb4 в изолированном ignored каталоге, не с текущим установленным owner. Benchmark — фиксированный доверенный тестовый код прежней версии, не runtime fallback и не восстановленный публичный VBA tool. Первая baseline попытка отказала creating_gms на длинном run path, cleanup true; следующие диагностические проверки выявили ошибку harness: единицы и ожидаемую обводку. Все собственные docs закрыты, unknown/replay нет. Успешный fresh case: C:/Users/olegiy/AppData/Local/CorelMcp_Perf_ffa061e1/performance.json. Цена ошибки: одиночная выборка операций не является статистическим benchmark; нельзя обещать общее ускорение.

- Final: Ruling: универсальные Corel types/live effects остаются вне scope, но обычные импортированные bitmap включены в transform/duplicate/group/layers/align/grid/layout. Цена ошибки: сложные эффекты/PowerClip по-прежнему требуют отдельной операции и могут давать UNSUPPORTED_SHAPE, а не частично изменяться.

- Final: Ruling: универсальная PDF/SVG fidelity не гарантируется; import_diagnostics возвращает наблюдаемые шрифты/число native effects, а source/font/effect fidelity всегда unknown. Цена ошибки: нужна визуальная проверка preview, наличие установленного шрифта не доказывает отсутствие подмены фильтром.

- Final: Ruling: ускорение всех команд не является gate, реальные регрессии создания/измерения сохраняются численно в performance report. Цена ошибки: большие batch могут быть существенно медленнее прежнего внутри-Corel VBA loop; общего обещания ускорения нет.

- Final: Ruling: preexisting webui design diff не относится к миграции, сохраняется; reviewer проверил только согласованную минимальную интеграцию. Цена ошибки: прежние особенности UI не считаются заново спроектированными/исправленными этой задачей.

- Final: Ruling: юридическую достаточность права interop код-review не подтверждает; licensing/publish/clean-install gates остаются незавершёнными. Цена ошибки: распространяемый пакет не выпускается до применимого официального подтверждения.

- Final: Ruling: failed admission требует доказанного appliedfalse и отсутствия partial; GMS absence proof не является доказательством исхода мутации. Для истории с appliedtrue/null обновление остаётся запрещено, файлы/исходы не переписываются. Цена ошибки: консервативный отказ даже после известной частичной мутации; оператор должен разрешить outcome вне нового installer, автоматического обхода нет.

- Final: Ruling: private import COM objects должны иметь отдельный scope, освобождаемый до закрытия собственного документа — lifetime RCW шрифтов/фигур важнее общего cleanup-at-end шаблона. Цена ошибки: неверный порядок может обрушить Corel; подтверждение исправления требует исходного SVG с текстом, повторений и полного live suite, а не одного удачного запуска.

## Отложенные Minor

Нет: reviewer не оставил замечаний Minor.
