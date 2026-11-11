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

## Исполнение

Сервер публикует все 18 инструментов без разрешительного config и подтверждений.
Старый `%LOCALAPPDATA%\CorelDrawMcp\config.json` не читается, не изменяется и не
удаляется; исторический `tool_policy` в прежних отчётах остаётся данными.
`readOnlyHint`/`destructiveHint` описывают действия, а не предоставляют разрешение.
Внешний MCP-клиент может использовать собственные подтверждения.

Пользовательские абсолютные Windows-пути не требуют регистрации или включения
в разрешённый каталог. Проверки схем, точной цели, внутренних служебных путей,
идемпотентности и cleanup сохраняются. VBA имеет полный доступ к системе и
не является sandbox. [Панель агента](docs/agent-panel.md) исполняет команды
сразу; контекст и доступный проверенный PNG передаются выбранному provider
автоматически, без permission-checkbox. Защита API-ключей остаётся обязательной.

## Повтор, cleanup и unknown

Изменения требуют `request_id`; одинаковый ID с другими параметрами отклоняется. `timed_out` и `unknown` не разрешают повтор мутации. Если `succeeded` сопровождается `cleanup.completed=false`, действие уже применено, но восстановление состояния неполно; сохранены основная ошибка, `cleanup.errors`, предупреждения и артефакты. Проверьте документ и состояние приложения, затем исправьте cleanup без повторного изменения.

Артефакты находятся в `%LOCALAPPDATA%\CorelDrawMcp\runs` либо `CORELDRAW_MCP_RUNS`. `corel_list_artifacts` возвращает bytes/возраст/защиту и `prune_token`. `corel_prune_artifacts` удаляет только внутренние файлы с неизменённым токеном; unknown, timed_out, незавершённые, cleanup с ошибкой и retained GMS защищены. Перед удалением сохраняется компактный report с `details_pruned=true`; reservation/request ID остаётся, поэтому повтор не запускается. Внешние CDR/экспорты сохраняются. `corel_export_diagnostics` пишет внешний атомарный allowlist-отчёт без кода, путей, имён, request ID, result и raw error text.

## Измерения и экспорт

`corel_measure_shapes` измеряет векторные фигуры в изолированной копии: per-object и aggregate minimum-area rectangle в мм. `boundary_error_mm=tolerance_mm`, `dimension_error_mm≤2*tolerance_mm`; лимит 200000 точек. PowerClip, bitmap, lens, transparency и live effects возвращают явную ошибку. Группы считаются одним объектом, occlusion не учитывается.

`corel_export` поддерживает девять форматов, страницы/выделение и PDF-документ. JPG/TIFF принимают RGB/CMYK, DPI и профиль; JPG compression, TIFF LZW/none и transparency. AI/EPS сохраняют текст или используют кривые. PNG ограничен 40 МП. Save As и экспорт автоматически заменяют существующий файл; параметра `overwrite` у этих tools нет. Экспорт публикуется через staging после успешного Finish. Атомарность нативного SaveAs не обещается; путь, открытый в другом документе, отклоняется.

## VBA и документация

Независимый наблюдатель VBA включён по умолчанию в режиме `observe`. Он
публикует безопасную сводку `vba_observer` через `corel_get_job`, пока COM
заблокирован, и не закрывает окна. Для отключения задайте
`$env:CORELDRAW_MCP_VBA_OBSERVER='off'` перед запуском сервера/панели.
Значение `compile-errors` пока отклоняется: подтверждённых профилей нет.
Настройка применяется к новому процессу и не разрешает replay текущего job.
При `requires_operator` осмотрите соответствующее окно вручную и продолжайте
опрос того же задания. Сырой локальный `vba-observer.jsonl` автоматически
модели не передаётся. Подробности — в [справке VBA](skills/coreldraw-automation/references/vba.md#наблюдение-блокирующих-окон).

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
npm run test:integration:observer
```

`npm test` проверяет stdio, схемы, idempotency, mutex, отчёты и синтетический worker. `npm run test:integration` использует только временные документы и сохраняет диагностику. После живых тестов удаляйте только временные CorelDRAW GMS/проекты `CorelMcp_*`; установленные сторонние GMS и пользовательские документы не трогайте. Не утверждайте live-проверку CorelDRAW, пока не просмотрен фактический `verification.json`.

`test:integration:observer` без `CORELDRAW_MCP_LIVE_OBSERVER=1` явно пропускает
живой запуск до подключения к CorelDRAW. При включении создаёт только свой
документ, проверяет метаданные HWND и успешные VBA-операции в observe.
`CORELDRAW_MCP_LIVE_OBSERVER_INTERACTIVE=1` дополнительно включает Err.Raise
с присутствующим оператором; Error Trapping не меняется. Неизвестный исход
сохраняется в отчёте, транспорт не убивает COM-процесс. Модальные профили
Compile/MsgBox и сохранность всех shape/selection/page пользовательских
документов требуют отдельного исследования и этим smoke не подтверждаются.

Результаты версии 0.2.0: [отчёт проверки](docs/verification-0.2.0.md). Векторный PLT использует 1000 единиц/дюйм для согласованного физического масштаба при повторном импорте; это не настройка оборудования.

## Лицензия

Собственный код проекта распространяется под [MIT](LICENSE),
Copyright (c) 2026 CorelDRAW-MCP contributors. Разрешены использование,
изменение, распространение и продажа при сохранении уведомления об авторских
правах и текста лицензии. Программа предоставляется без гарантий.

Происхождение заимствований и уведомление AI for You сохранены в
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). При распространении сохраняйте
оба файла и лицензионные уведомления включённых сторонних зависимостей;
MIT проекта не заменяет их условия и не распространяется на CorelDRAW.
CorelDRAW устанавливается и лицензируется отдельно.

Python-пакет включает копии `webhost/LICENSE` и
`webhost/THIRD_PARTY_NOTICES.md`; при изменении исходных уведомлений обновляйте
эти копии одновременно. Поле `private: true` в npm-пакетах сохранено:
выбор MIT не включает публикацию в npm автоматически.
