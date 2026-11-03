# Краткая инструкция

## 1. Установка

Нужны Windows, CorelDRAW 27 с VBA, Node.js 24, Codex CLI и Windows PowerShell 5.1.

В PowerShell из корня проекта выполните:

```powershell
powershell -NoProfile -File .\scripts\Install-CorelMcp.ps1
```

Перезапустите Codex CLI после установки. CorelDRAW должен быть запущен в интерактивной Windows-сессии.

## 2. Обычный порядок работы

1. Вызовите `corel_status` и получите точный `document_id`.
2. Откройте файл через `corel_open_document` или создайте его через `corel_create_document`.
3. Изучите содержимое через `corel_inspect_document`.
4. Для изображения используйте `corel_preview`; для результата в файл — `corel_export`.
5. После изменения сохраните документ через `corel_save_document`.
6. Закройте созданный документ через `corel_close_document`.

Изменяющие вызовы требуют уникального `request_id`. Каждый вызов возвращает `job_id`; результат получите через `corel_get_job(job_id, wait_seconds=20)`.

Пример последовательности:

```text
corel_status
corel_open_document(request_id="open-001", path="C:\\Работа\\макет.cdr")
corel_inspect_document(document_id="полученный-id")
corel_export(request_id="export-001", document_id="полученный-id",
             path="C:\\Работа\\макет.pdf", format="pdf", range="page")
corel_save_document(request_id="save-001", document_id="полученный-id")
```

## 3. Важные правила

- `corel_export` поддерживает `png`, `svg`, `pdf`, `jpg`, `tiff`, `ai`, `eps`, `dxf`, `plt`.
- Для превью выделения укажите `range="selection"`; при пустом выделении будет ошибка.
- `corel_measure_shapes` измеряет поддерживаемые векторные объекты в миллиметрах.
- Не повторяйте автоматически задания со статусом `timed_out` или `unknown`.
- При `cleanup.completed=false` операция уже могла выполниться; сначала проверьте документ и отчёт.
- VBA имеет полный доступ к CorelDRAW и Windows. Выполняйте только доверенный код.

## 4. Справка и очистка

- `corel_guide(topic="tools")` — справка по инструментам; без `topic` возвращается оглавление.
- `corel_list_artifacts` — список служебных артефактов.
- `corel_prune_artifacts` — удаление только явно выбранных и проверенных артефактов.
- `corel_export_diagnostics` — локальный диагностический отчёт без содержимого макета.

Полные параметры находятся в [справочнике инструментов](../skills/coreldraw-automation/references/tools.md).
