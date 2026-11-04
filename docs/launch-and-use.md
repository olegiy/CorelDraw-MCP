# Запуск и работа с MCP-сервером CorelDRAW

Эта инструкция предназначена для Windows и CorelDRAW 27. Сервер работает локально через MCP-транспорт `stdio`: Codex запускает `node dist/index.js`, а сервер передаёт операции CorelDRAW отдельному PowerShell 5.1 STA-исполнителю.

## Требования

- Windows с интерактивной пользовательской сессией;
- CorelDRAW 27 с установленным VBA;
- Node.js 24.x;
- Windows PowerShell 5.1;
- Codex CLI, доступный в `PATH`.

CorelDRAW должен быть запущен в той же интерактивной сессии Windows. Сервер не запускает и не закрывает приложение автоматически.

## Установка и регистрация в Codex

Откройте PowerShell в каталоге проекта и выполните:

```powershell
powershell -NoProfile -File .\scripts\Install-CorelMcp.ps1
```

Установщик устанавливает зависимости (`npm ci --ignore-scripts`), собирает TypeScript, регистрирует MCP-сервер с именем `coreldraw` и подключает глобальный skill `coreldraw-automation`. Перед изменением конфигурации Codex создаётся резервная копия. После установки откройте новый сеанс Codex.

Проверить регистрацию:

```powershell
codex mcp list
codex mcp get coreldraw
```

В выводе должна быть команда Node.js и путь к `dist\index.js` этого проекта. Удалить регистрацию и junction skill можно так:

```powershell
powershell -NoProfile -File .\scripts\Install-CorelMcp.ps1 -Uninstall
```

Исходники, документы и служебные артефакты при удалении не стираются.

## Запуск без установщика

Для ручной проверки сборки:

```powershell
npm ci --ignore-scripts
npm run build
node .\dist\index.js
```

Последняя команда запускает stdio-сервер и ожидает MCP-сообщения на стандартном вводе. В обычной работе её запускает Codex по зарегистрированной записи. Не направляйте журналы в stdout: протокол MCP использует его для сообщений сервера.

## Первый сеанс

После подключения используйте skill `$coreldraw-automation` или вызовите инструменты в такой последовательности:

1. `corel_status` — проверить версию CorelDRAW, VBA, исполнитель и получить точные `document_id`.
2. `corel_open_document` или `corel_create_document` — открыть существующий либо создать новый документ.
3. `corel_inspect_document` — прочитать страницы и объекты перед изменением.
4. `corel_preview` — получить PNG-превью страницы или непустого выделения.
5. `corel_export` — экспортировать в `png`, `svg`, `pdf`, `jpg`, `tiff`, `ai`, `eps`, `dxf` или `plt`.
6. `corel_save_document` — сохранить документ явной командой.
7. `corel_close_document` — закрыть созданный документ, когда работа завершена.

Пример запроса:

```text
corel_status
corel_open_document(request_id="open-001", path="C:\\Работа\\макет.cdr")
corel_inspect_document(document_id="полученный-id")
corel_export(request_id="export-001", document_id="полученный-id",
             path="C:\\Работа\\макет.pdf", format="pdf", range="page")
corel_save_document(request_id="save-001", document_id="полученный-id")
```

Все пути должны быть абсолютными Windows-путями. Для существующего файла требуется `overwrite=true`. Закрытие изменённого документа требует предварительного сохранения либо `discard_changes=true`.

## Задания, повторы и ошибки

Операции CorelDRAW, экспорт, VBA и сборка возвращают `job_id`. Получайте результат через `corel_get_job(job_id, wait_seconds=20)`. Опрос читает сохранённый отчёт и не обращается к COM. Проверяйте `status`, `result`, `error`, `cleanup`, `warnings` и `artifacts`.

Изменяющие вызовы требуют нового уникального `request_id`. Повтор с теми же аргументами возвращает исходное задание; изменение аргументов отклоняется. Задания `timed_out` и `unknown` нельзя автоматически повторять: сначала проверьте документ и состояние CorelDRAW. Если `cleanup.completed=false`, действие уже могло выполниться — повтор также запрещён до проверки.

## VBA и измерения

`corel_measure_shapes` измеряет поддерживаемые векторные объекты в миллиметрах. `corel_build_vba` проверяет исходники `.bas`, а `corel_execute_vba` выполняет модуль с `Public Function Main() As String` и JSON-результатом. VBA имеет полный доступ к CorelDRAW и операционной системе; запускайте только доверенный код и не используйте `Application.Quit`, shell или диалоги.

## Конфигурация и обслуживание

Параметры политики находятся в `%LOCALAPPDATA%\CorelDrawMcp\config.json`. Единственный ключ — массив `disabled_tools`, например:

```json
{"disabled_tools":["corel_execute_vba","corel_export"]}
```

Настройка применяется после перезапуска сервера. `corel_status`, `corel_get_job`, `corel_guide` и `corel_list_artifacts` доступны всегда. Некорректная конфигурация переводит сервер в безопасный режим только для чтения.

Справка: `corel_guide(topic="tools")`, `corel_guide(topic="vba")`, `corel_guide(topic="install")`. Служебные файлы перечисляет `corel_list_artifacts`; удаление выполняется только выбранными токенами через `corel_prune_artifacts`. Диагностику можно выгрузить через `corel_export_diagnostics`.

## Проверка установки

```powershell
npm test
npm run check:contracts
```

Интеграционные тесты выполняйте только при доступном CorelDRAW:

```powershell
npm run test:integration
```

