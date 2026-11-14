# corel-mcp

Передача ветки и запуск живых тестов на другом компьютере: [инструкция для агента](docs/handoff-live-testing.md).

Локальный MCP-сервер и скилл для CorelDRAW **27**, Node.js **24**, Windows x64.
TypeScript ведёт задания/артефакты и вычисляет геометрию; C# .NET 10 выполняет
готовые типизированные операции на одном STA-потоке. В разработке гибридного режима
фиксированный готовый VBA-модуль применяет пакеты внутри Corel. VBE/компиляция и
PowerShell COM во время запросов не используются; произвольный код не принимается.

Гибрид 0.4.0 находится в исходниках и проходит итоговые испытания. Режим по умолчанию — `csharp` до завершения всех gates; для локального гибрида задайте `CORELDRAW_MCP_EXECUTION_MODE=hybrid`.
Выпуск распространяемого готового пакета
заблокирован до подтверждения прав на interop: [статус и подготовленный запрос](worker/INTEROP-DISTRIBUTION.md).
Установка в пользовательское окружение этой работой не выполнялась.

## Сборка и установка

Из исходников нужны SDK **10.0.404**, официально установленная VGCore 27 type library
и Windows PowerShell 5.1/.NET Framework только для build-time генерации interop.

```powershell
npm ci --ignore-scripts
npm run build
powershell -NoProfile -File scripts/build-worker.ps1 -DotnetPath '<путь к dotnet.exe SDK 10.0.404>'
```

Для разработки задайте `CORELDRAW_MCP_CSHARP_WORKER` абсолютным путём к построенному EXE.
Для framework-dependent сборки нужен `DOTNET_ROOT_X64`, указывающий на локальный SDK/runtime 10.
Единственный backend — csharp; старый powershell отклоняется без fallback.

Готовая поставка должна содержать self-contained `worker/publish/win-x64/CorelMcp.Worker.exe`
с зависимостями и проверенным manifest; получателю не нужны SDK, глобальный .NET runtime или VBA.
До закрытия лицензионного gate такой пакет не публикуется. Инсталляторы должны пройти
read-only pre-upgrade и version handshake перед регистрацией/активацией. Они сохраняют
историю, чужие MCP-записи, пользовательские рисунки и несвязанные скиллы.

```powershell
powershell -NoProfile -File scripts/Install-CorelMcp.ps1
```

Отдельная [панель агента](docs/agent-panel.md) использует Python 3.12 и собранный React UI;
установка панели не запускает CorelDRAW и не регистрирует Codex автоматически.
[Запуск](docs/launch-and-use.md), [краткая памятка](docs/quick-start.md).

## 39 инструментов

| Группа | Возможности |
| --- | --- |
| Документы | status, inspect, create/open/save/close точного документа |
| Рисование | create_shapes, transform, duplicate, style, layers, group, boolean, create_text, convert_to_curves |
| Производство | inspect/create/edit curve, find/edit text, preflight |
| Раскладка | align_shapes, grid_duplicate, layout_shapes dry_run/apply |
| Сборка | import SVG/PDF/PNG/JPEG, add/resize pages, PowerClip place/extract |
| Экспорт/API | export PNG/PDF/SVG/JPEG/TIFF/AI/EPS/DXF/PLT, preview, describe_api, measure_shapes |
| Локальные данные | add_note, export_diagnostics, list/prune artifacts, get_job, guide |

Полные строгие схемы: MCP tools/list и [справочник](skills/coreldraw-automation/references/tools.md).
[Скилл](skills/coreldraw-automation/SKILL.md), [готовые операции](skills/coreldraw-automation/references/operations.md),
[JSON-рецепты](skills/coreldraw-automation/references/recipes.md), [API 27](skills/coreldraw-automation/references/api-v27.md).

## Задания и состояние

COM-операции возвращают job_id; corel_get_job читает сохранённый отчёт без COM.
Новой мутации нужен уникальный request_id. Повтор принятого запроса возвращает прежний Job,
изменённые аргументы дают конфликт. Deadline не прерывает COM и не освобождает слот.
После timed_out/unknown/partial нет автоматического replay, undo или переключения backend.
Проверяйте фактические IDs, error.applied, cleanup.completed, warnings и артефакты.

Цели выбираются по точному document_id и StaticID; page/layer IDs непрозрачны и принадлежат сеансу.
Координаты _mm идут от нижнего левого угла страницы независимо от Unit/origin/refpoint.
Настройки, активная страница и живое выделение восстанавливаются отдельно от основной ошибки.
Dirty не сбрасывается для маскировки изменений. Даже BeginCommandGroup может изменить Dirty.

Текст вне BMP, NUL и одиночные суррогаты отклоняются до мутации; кириллица поддерживается.
Шрифты не подменяются автоматически. Preflight unknown — неполная проверка, не passed.
Layout — прямоугольный shelf без фигурного nesting. Измеритель PowerClip не поддерживает.
Native import не является sandbox; внешние SVG resources и PDF с паролем отклоняются.

Нет разрешительного config и новых UI-переключателей разрешений. Старый config не читается
и не удаляется. Save As/export заменяют назначение без поля overwrite; экспорт использует staging,
нативный SaveAs не обещает атомарности. Панель отправляет контекст и проверенный PNG выбранному
provider автоматически; ключи остаются в Credential Manager и не должны попадать в аргументы.

Исторические отчёты, compiler/observer metadata и retained GMS читаются как данные.
Старый код не переисполняется. Активный owner, pending/unknown и неподтверждённая очистка
блокируют обновление. Их исход разрешается прежней версией/оператором без переписывания отчётов.
Prune защищает незавершённые и retained записи, внешние CDR/экспорты не удаляет.
CorelDRAW не завершается через Application.Quit; чужие рисунки и GMS не затрагиваются.

## Проверки

```powershell
& '<dotnet.exe SDK 10.0.404>' test worker/CorelMcp.Worker.Tests -c Release
npm test
npm run check:contracts
powershell -NoProfile -File scripts/Verify-CorelAgent.ps1
$env:CORELDRAW_WORKER_LIVE = '1'
npm run test:integration
```

Проверка исходников до выпуска пакета: `Verify-CorelAgent.ps1 -SourceOnly -DotnetPath '<dotnet.exe SDK 10.0.404>'`.
Она выполняет C#, Node, host и browser suites, но не заменяет приёмку готового комплекта.
Публикация через `scripts/Publish-CorelWorker.ps1` сначала требует проверенную запись
прав на interop; текущий статус и проект запроса — в [INTEROP-DISTRIBUTION.md](worker/INTEROP-DISTRIBUTION.md).

Live suites запускаются последовательно и создают только собственные временные документы.
Отчёты/PNG сохраняются в artifacts. Отдельный panel live smoke требует
CORELDRAW_AGENT_LIVE=1 и проверенный CORELDRAW_AGENT_LIVE_FIXTURE; обычный Verify его не заменяет.
Подробности: [архитектура](docs/architecture.md), [worker](worker/README.md).
Отчёт [0.2.0](docs/verification-0.2.0.md) является историческим и не подтверждает новый релиз.
Результаты предыдущей миграции: [проверка исходников 0.3.0](docs/verification-0.3.0.md). Текущий ход гибрида: [план 0.4.0](.kilo/plans/2026-11-13-corel-hybrid-runtime.md).

### Разработка гибридного исполнения

Новые `corel_create_batch` / `corel_update_batch` принимают до 1000 элементов и требуют `CORELDRAW_MCP_EXECUTION_MODE=hybrid` в окружении сервера. Режим фиксируется при запуске worker; в ходе внедрения default остаётся `csharp`. Маршрут и версия модуля возвращаются в диагностике. До вызова проверяется готовый GMS/manifest; отсутствие или несовпадение дают отказ до мутации, без fallback. Новая версия ещё проходит реализацию и финальные gates; это не сообщение о выпуске/установке готового пакета.
