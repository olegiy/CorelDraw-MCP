# corel-mcp

Локальный MCP-сервер для **CorelDRAW 27**: документы CDR, ограниченная инспекция объектов, измерения геометрии, экспорт, VBA и безопасное чтение typelib. Все пользовательские сообщения и отчёты — на русском.

## Установка

Требуются Windows с интерактивной сессией, Node.js 24, Codex CLI, CorelDRAW 27 с VBA и Windows PowerShell 5.1. Из корня проекта выполните:

```powershell
powershell -NoProfile -File .\scripts\Install-CorelMcp.ps1
```

Установщик выполнит `npm ci --ignore-scripts`, сборку, регистрацию `coreldraw` и junction глобального скилла `coreldraw-automation`. Перед изменением Codex config создаётся резервная копия. Несвязанные MCP-записи и чужой каталог скилла не перезаписываются. Удаление выполняется тем же скриптом с `-Uninstall`; исходники, документы и артефакты сохраняются.

## 18 инструментов

Короткая инструкция для первого запуска: [docs/quick-start.md](docs/quick-start.md).

| Группа | Инструменты |
| --- | --- |
| CorelDRAW | `corel_status`, `corel_inspect_document`, `corel_create_document`, `corel_open_document`, `corel_save_document`, `corel_close_document` |
| Экспорт | `corel_export` (PNG/SVG/PDF/JPG/TIFF/AI/EPS/DXF/PLT), `corel_preview` |
| VBA/API | `corel_build_vba`, `corel_execute_vba`, `corel_describe_api`, `corel_measure_shapes` |
| Локальные операции | `corel_add_note`, `corel_export_diagnostics`, `corel_list_artifacts`, `corel_prune_artifacts` |
| Отчёты и справка | `corel_get_job`, `corel_guide`; ресурсы `coreldraw://guide/<topic>` |

Все COM-вызовы возвращают `job_id`. Используйте `corel_get_job` с `wait_seconds` до 20; polling не вызывает COM. Схемы и ограничения доступны через MCP `tools/list` и [справочник инструментов](skills/coreldraw-automation/references/tools.md).

## Повтор, cleanup и unknown

Изменения требуют `request_id`; одинаковый ID с другими параметрами отклоняется. `timed_out` и `unknown` не разрешают повтор мутации. Если `succeeded` сопровождается `cleanup.completed=false`, действие уже применено, но восстановление состояния неполно; сохранены основная ошибка, `cleanup.errors`, предупреждения и артефакты. Проверьте документ и состояние приложения, затем исправьте cleanup без повторного изменения.

Артефакты находятся в `%LOCALAPPDATA%\CorelDrawMcp\runs` либо `CORELDRAW_MCP_RUNS`. `corel_list_artifacts` возвращает bytes/возраст/защиту и `prune_token`. `corel_prune_artifacts` удаляет только внутренние файлы с неизменённым токеном; unknown, timed_out, незавершённые, cleanup с ошибкой и retained GMS защищены. Перед удалением сохраняется компактный report с `details_pruned=true`; reservation/request ID остаётся, поэтому повтор не запускается. Внешние CDR/экспорты сохраняются. `corel_export_diagnostics` пишет внешний атомарный allowlist-отчёт без кода, путей, имён, request ID, result и raw error text.

## Измерения и экспорт

`corel_measure_shapes` измеряет векторные фигуры в изолированной копии: per-object и aggregate minimum-area rectangle в мм. `boundary_error_mm=tolerance_mm`, `dimension_error_mm≤2*tolerance_mm`; лимит 200000 точек. PowerClip, bitmap, lens, transparency и live effects возвращают явную ошибку. Группы считаются одним объектом, occlusion не учитывается.

`corel_export` поддерживает девять форматов, страницы/выделение и PDF-документ. JPG/TIFF принимают RGB/CMYK, DPI и профиль; JPG compression, TIFF LZW/none и transparency. AI/EPS сохраняют текст или используют кривые. PNG ограничен 40 МП; экспорт идёт через staging и требует `overwrite=true` для существующего пути.

## VBA и документация

VBA выполняется в отдельном временном GMS с внешней Undo-группой, восстановлением настроек и событий. Это не sandbox: код имеет полный доступ к CorelDRAW и ОС. Установленные GMS не редактируются. Используйте `Option Explicit`, `Main() As String`, валидный JSON, `Err.Raise`, точные document ID и StaticID. Не вызывайте `Application.Quit`, shell, диалоги или вложенный Undo.

- [Скилл](skills/coreldraw-automation/SKILL.md)
- [Инструменты](skills/coreldraw-automation/references/tools.md)
- [VBA](skills/coreldraw-automation/references/vba.md)
- [API 27](skills/coreldraw-automation/references/api-v27.md)
- [Рецепты](skills/coreldraw-automation/references/recipes.md)
- [Подводные камни](skills/coreldraw-automation/references/pitfalls.md)
- [Архитектура](docs/architecture.md)

## Проверка

```powershell
npm ci --ignore-scripts
npm test
npm run test:integration
node test/integration-extended.mjs
```

`npm test` проверяет stdio, схемы, idempotency, mutex, отчёты и синтетический worker. `npm run test:integration` использует только временные документы и сохраняет диагностику. Не утверждайте live-проверку CorelDRAW, пока не просмотрен фактический `verification.json`.

Результаты версии 0.2.0: [отчёт проверки](docs/verification-0.2.0.md). Векторный PLT использует 1000 единиц/дюйм для согласованного физического масштаба при повторном импорте; это не настройка оборудования.
