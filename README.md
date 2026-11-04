# corel-mcp

Локальный MCP-сервер для **CorelDRAW 27**: документы CDR, ограниченная инспекция объектов, измерения геометрии, экспорт, VBA и безопасное чтение typelib. Все пользовательские сообщения и отчёты — на русском.

## Установка

Требуются Windows с интерактивной сессией, Node.js 24, Codex CLI, CorelDRAW 27 с VBA и Windows PowerShell 5.1. Из корня проекта выполните:

```powershell
powershell -NoProfile -File .\scripts\Install-CorelMcp.ps1
```

Установщик выполнит `npm ci --ignore-scripts`, сборку, регистрацию `coreldraw` и junction глобального скилла `coreldraw-automation`. Перед изменением Codex config создаётся резервная копия. Несвязанные MCP-записи и чужой каталог скилла не перезаписываются. Удаление выполняется тем же скриптом с `-Uninstall`; исходники, документы и артефакты сохраняются.

## 18 инструментов

Инструкция по установке, запуску и работе: [docs/launch-and-use.md](docs/launch-and-use.md). Краткая памятка: [docs/quick-start.md](docs/quick-start.md).

| Группа | Инструменты |
| --- | --- |
| CorelDRAW | `corel_status`, `corel_inspect_document`, `corel_create_document`, `corel_open_document`, `corel_save_document`, `corel_close_document` |
| Экспорт | `corel_export` (PNG/SVG/PDF/JPG/TIFF/AI/EPS/DXF/PLT), `corel_preview` |
| VBA/API | `corel_build_vba`, `corel_execute_vba`, `corel_describe_api`, `corel_measure_shapes` |
| Локальные операции | `corel_add_note`, `corel_export_diagnostics`, `corel_list_artifacts`, `corel_prune_artifacts` |
| Отчёты и справка | `corel_get_job`, `corel_guide`; ресурсы `coreldraw://guide/<topic>` |

Все COM-вызовы возвращают `job_id`. Используйте `corel_get_job` с `wait_seconds` до 20; polling не вызывает COM. Схемы и ограничения доступны через MCP `tools/list` и [справочник инструментов](skills/coreldraw-automation/references/tools.md).

## Локальная конфигурация

При запуске читается `%LOCALAPPDATA%\CorelDrawMcp\config.json` (для отдельного
окружения можно задать `CORELDRAW_MCP_CONFIG_ROOT`). Без файла включены все
инструменты. Единственный ключ конфигурации: `disabled_tools`, массив уникальных
известных имён, например `{"disabled_tools":["corel_execute_vba","corel_export"]}`.
Изменение применяется после перезапуска сервера; отключённые tools скрыты из
`tools/list`, вызов не принимается и не создаёт задание.

`corel_status`, `corel_get_job`, `corel_guide` и `corel_list_artifacts` всегда
доступны для диагностики и восстановления. Остальные инспекции и preview можно
отключать явно. Некорректный JSON, неизвестные ключи/имена, повторы, размер более
64 КиБ, недоступный путь, ссылка или junction дают `source=invalid`: остаются
только read-only инструменты, включая preview. Сводка `tool_policy` в отчёте
`corel_status` содержит effective/registered/total/disabled counts и сохраняется
при опросе задания.

Для атомарного обновления без записи рядом с установленным кодом:

```powershell
node --input-type=module -e "import {writeToolPolicy} from './dist/config.js'; import {join} from 'node:path'; writeToolPolicy(join(process.env.LOCALAPPDATA,'CorelDrawMcp'),{disabled_tools:['corel_export']});"
```

Это только сокращение поверхности MCP, не аутентификация и не sandbox.
Read-only не означает отсутствие временных файлов или обращения к COM;
разрешённый VBA по-прежнему имеет полный доступ к системе.

## Повтор, cleanup и unknown

Изменения требуют `request_id`; одинаковый ID с другими параметрами отклоняется. `timed_out` и `unknown` не разрешают повтор мутации. Если `succeeded` сопровождается `cleanup.completed=false`, действие уже применено, но восстановление состояния неполно; сохранены основная ошибка, `cleanup.errors`, предупреждения и артефакты. Проверьте документ и состояние приложения, затем исправьте cleanup без повторного изменения.

Артефакты находятся в `%LOCALAPPDATA%\CorelDrawMcp\runs` либо `CORELDRAW_MCP_RUNS`. `corel_list_artifacts` возвращает bytes/возраст/защиту и `prune_token`. `corel_prune_artifacts` удаляет только внутренние файлы с неизменённым токеном; unknown, timed_out, незавершённые, cleanup с ошибкой и retained GMS защищены. Перед удалением сохраняется компактный report с `details_pruned=true`; reservation/request ID остаётся, поэтому повтор не запускается. Внешние CDR/экспорты сохраняются. `corel_export_diagnostics` пишет внешний атомарный allowlist-отчёт без кода, путей, имён, request ID, result и raw error text.

## Измерения и экспорт

`corel_measure_shapes` измеряет векторные фигуры в изолированной копии: per-object и aggregate minimum-area rectangle в мм. `boundary_error_mm=tolerance_mm`, `dimension_error_mm≤2*tolerance_mm`; лимит 200000 точек. PowerClip, bitmap, lens, transparency и live effects возвращают явную ошибку. Группы считаются одним объектом, occlusion не учитывается.

`corel_export` поддерживает девять форматов, страницы/выделение и PDF-документ. JPG/TIFF принимают RGB/CMYK, DPI и профиль; JPG compression, TIFF LZW/none и transparency. AI/EPS сохраняют текст или используют кривые. PNG ограничен 40 МП; экспорт идёт через staging и требует `overwrite=true` для существующего пути.

## VBA и документация

Адресные рецепты используют общий helper без Main; константы StaticID заменяются
только на проверенные числа. `error.applied=false` у recipe validation означает
отказ до изменяющего вызова, true означает возможное частичное действие.
Ошибка recipe cleanup отмечается отдельно. Произвольный VBA может менять любые
документы: передача document_id и post-call TARGET_DOCUMENT_CHANGED не являются
изоляцией или запретом побочных действий.

У `corel_export_diagnostics` путь необязателен: без path создаётся уникальный
JSON в каталоге diagnostics рядом с runs, вне reservation/job directory.
Никакие данные не отправляются наружу. Расширенные подробности доступны только
явным чтением исходного job report/log; краткий отчёт не является гарантией
полного обезличивания произвольных данных.

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

`npm test` проверяет stdio, схемы, idempotency, mutex, отчёты и синтетический worker. `npm run test:integration` использует только временные документы и сохраняет диагностику. После живых тестов удаляйте только временные CorelDRAW GMS/проекты `CorelMcp_*`; установленные сторонние GMS и пользовательские документы не трогайте. Не утверждайте live-проверку CorelDRAW, пока не просмотрен фактический `verification.json`.

Результаты версии 0.2.0: [отчёт проверки](docs/verification-0.2.0.md). Векторный PLT использует 1000 единиц/дюйм для согласованного физического масштаба при повторном импорте; это не настройка оборудования.
