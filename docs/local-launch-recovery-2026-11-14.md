# Восстановленный локальный запуск — 14 ноября 2026

## Причина расхождения

Основной checkout и прежний worktree находятся на `0d172838ed4d800c5ad14f94193424385f3aed85`. Переключение Git не переносит игнорируемые `dist`, `worker/bin`, generated interop и локальный SDK. Поэтому основной `dist/index.js` остался от PowerShell-исполнителя, а проверенные артефакты сохранились в прежнем worktree. Повторная реализация гибрида не требовалась.

## Найденные компоненты

Общий префикс прежнего worktree:

`C:\Users\olegiy\CorelDRAW-MCP\coreldraw-mcp\.kilo\worktrees\codex-like-ui-task-1`

| Компонент | Путь относительно префикса |
| --- | --- |
| SDK | `.superpowers\sdd\1794339253999-csharp-worker-typed-corel-tools\dotnet\dotnet.exe` |
| Framework-dependent worker | `worker\CorelMcp.Worker\bin\Release\net10.0-windows\CorelMcp.Worker.exe` |
| Interop | `worker\generated\VGCore.Interop.dll` |
| Готовый GMS worker | `worker\CorelMcp.Worker\bin\Release\net10.0-windows\native\CorelMcp.Runtime.gms` |
| Проверенная прежняя TS-сборка | `dist\index.js` |

Живой `dotnet --info` подтвердил SDK **10.0.404**, runtime **10.0.15**, Windows x64. Манифест GMS содержит `CorelMcp_Runtime_v1_9b5348678d96`, source SHA `9b5348678d96ccb92bedc28e2ab84894b7b4998d7ca145a8b1c64d168dbd9157`, GMS SHA `126b41afa31e908d1b0008b87cfb18812ade20502fbb52408837b9ea5654d0f9`. В режиме hybrid worker успешно подтвердил готовность native module. Гибридный `corel_status` сам выполняется C#-маршрутом; его успех не заменяет live batch-тест.

## Подготовленный отдельный запуск

Из текущих `src` собран **отдельный** сервер:

`artifacts\local-launch-20261114\dist\index.js`

В том же каталоге находятся `start.ps1`, локальный `package.json` для идентичности версии, `verify.mjs`, отчёты и `runs`. Скрипт запуска задаёт переменные только внутри своего процесса PowerShell, использует найденный worker и `DOTNET_ROOT_X64`, не меняет системный PATH или регистрацию MCP. По умолчанию выбран `csharp`; `hybrid` требует явного параметра.

Для read-only проверки из основного каталога:

```powershell
node artifacts/local-launch-20261114/verify.mjs
```

Точка stdio-запуска для отдельного клиента:

```powershell
pwsh -NoProfile -File artifacts/local-launch-20261114/start.ps1
```

Явный гибрид:

```powershell
pwsh -NoProfile -File artifacts/local-launch-20261114/start.ps1 -Mode hybrid
```

Каталог `artifacts` игнорируется Git; этот локальный запуск зависит от сохранности прежнего worktree и его SDK. Это не переносимый или распространяемый пакет. До разрешения исторических барьеров использовать данный запуск только для диагностики; не подключать к штатной панели и не выполнять новые мутации как обход admission.

## Свежая проверка

14 ноября 2026, 16:24 UTC, новый MCP-клиент и worker для каждого режима:

| Проверка | Факт |
| --- | --- |
| TypeScript текущих исходников | Отдельная сборка и `tsc --noEmit` завершились с кодом 0 |
| tools/list | 39 инструментов; `corel_create_batch` присутствует, `corel_execute_vba` отсутствует |
| csharp `corel_status` | `succeeded`, executor `ready`, STA, `.NET 10.0.15`, Corel **27.2.0.135** |
| hybrid `corel_status` | `succeeded`, execution_mode `hybrid`, native_module **0.4.0**, cleanup completed |
| Состояние документа | В обоих ответах `Безымянный-1`, dirty=false, одна страница; events=true, optimization=false |
| Готовая прежняя TS-сборка | `node --test test/*.test.mjs`: 150 passed, 2 skipped, 0 failed; contracts ok |
| Готовая прежняя .NET-сборка | `dotnet test ... --no-build --no-restore`: 166 passed, 52 live skipped, 0 failed |

Доказательства:

- `artifacts/local-launch-20261114/verification-csharp.json`, job `7d0cc15a-15b4-45ab-9db5-0bc596a17c98`;
- `artifacts/local-launch-20261114/verification-hybrid.json`, job `87e3dbe7-7cd5-43dd-82f2-f3bffc255e45`;
- отдельные исходные `report.json` и `run.log` в подкаталоге `runs`.

Исходные документы не редактировались. CorelDRAW не перезапускался. GMS не компилировался и не пересобирался. Проверка после чистого старта Corel и batch-regression в этой работе не выполнялись. Отдельного lint-скрипта в package.json нет; проверка типов выполнена напрямую штатным TypeScript.

## Что намеренно не активировано

Штатный основной `dist` и регистрация MCP **не изменены**: `npm start` из основного checkout всё ещё указывает на прежнюю неисправную сборку. Для перехода штатной панели нужен отдельный успешный pre-upgrade gate.

На оригинальной истории gate возвращал `HISTORY_NOT_CHECKPOINTED`. На диагностической копии после восстановления SQLite-журнала получен `MUTATION_OUTCOME_UNRESOLVED`. В старых failed-результатах отсутствуют оба подтверждения `applied=false`; сохранён также `CorelMcp_dc3c5d1173cb` после `COMPILATION_FAILED`. Это исторические данные, а не результат нового status. Они не исправлялись вручную, не удалялись и не переигрывались. Оригинальная история и reports сохранены.

Штатная активация остаётся отдельным незавершённым шагом: безопасно разрешить исторические операции и retained-проект штатным recovery-механизмом, затем повторить pre-upgrade и заменить основную сборку. Подготовленный read-only запуск доказывает доступность SDK/worker и устраняет неопределённость путей, но не снимает этот барьер.
