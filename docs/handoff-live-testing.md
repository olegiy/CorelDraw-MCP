# Передача другому агенту для живых тестов CorelDRAW

Ветка `codex-like-ui-task-1` репозитория `https://github.com/olegiy/CorelDraw-MCP.git` содержит исходники MCP-сервера и C# worker, фиксированный собственный VBA runtime, тесты и историю проверки. Начальный коммит гибридного этапа — `0d17283`; используйте актуальный HEAD ветки.

## Подготовка на другом компьютере

Нужны Windows x64, установленный и зарегистрированный CorelDRAW 27 с поддержкой VBA, Node.js 24, Git, Windows PowerShell 5.1 x64 и .NET SDK **10.0.404**. `global.json` закрепляет точную версию SDK. Предыдущие живые результаты получены на CorelDRAW 27.2.0.135; другую сборку Corel нужно указать в новом отчёте и проверять отдельно.

В новом пустом каталоге откройте PowerShell:

```powershell
git clone --branch codex-like-ui-task-1 https://github.com/olegiy/CorelDraw-MCP.git
Set-Location CorelDraw-MCP
git status -sb
node --version
dotnet --list-sdks
npm ci
```

Укажите путь к `dotnet.exe` из SDK 10.0.404; для локально установленного SDK подставьте его фактическое расположение:

```powershell
$dotnetExecutable = 'C:\Program Files\dotnet\dotnet.exe'
$env:DOTNET_ROOT_X64 = Split-Path -Parent $dotnetExecutable
& $dotnetExecutable --version
powershell.exe -NoProfile -File scripts/build-worker.ps1 -DotnetPath $dotnetExecutable
npm run build
```

Скрипт генерирует `worker/generated/VGCore.Interop.dll` из локальной зарегистрированной type library CorelDRAW и собирает `worker/CorelMcp.Worker/bin/Release/net10.0-windows/CorelMcp.Worker.exe`. Готовые DLL и `bin/` в Git не входят. Права на распространение interop пока не подтверждены: инструкция описывает локальную сборку, а не поставку готового пакета. Подробности — в [INTEROP-DISTRIBUTION.md](../worker/INTEROP-DISTRIBUTION.md). Собственный GMS и manifest из ветки связаны хешами с исходниками `worker/vba/`.

## Проверка и живой запуск

Сначала выполните проверки без живых COM-тестов:

```powershell
npm test
& $dotnetExecutable test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj -c Release
npm run check:contracts
```

Затем откройте CorelDRAW и укажите путь к worker, собранному на этой машине:

```powershell
$env:CORELDRAW_MCP_BACKEND = 'csharp'
$env:CORELDRAW_MCP_CSHARP_WORKER = Join-Path (Get-Location) 'worker\CorelMcp.Worker\bin\Release\net10.0-windows\CorelMcp.Worker.exe'
$env:CORELDRAW_WORKER_LIVE = '1'
$env:DOTNET_HOST_PATH = $dotnetExecutable

& $dotnetExecutable test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj -c Release --filter FullyQualifiedName~LiveSmoke
npm run test:integration
```

Для отдельной проверки гибридного маршрута при доступном и разрешённом VBA:

```powershell
node test/integration-hybrid.mjs
```

Этот более длительный сценарий явно выбирает `hybrid`, выполняет 100 пакетных операций и проверяет атлас из 1000 плашек. Стандартный режим сервера — `csharp`. Для запуска сервера в гибридном режиме задайте `$env:CORELDRAW_MCP_EXECUTION_MODE = 'hybrid'` и запускайте `npm start` из MCP-клиента по stdio.

Live-сценарии выполняются последовательно, без параллельного `npm test`: используется общий mutex Corel. Тесты создают собственные временные документы, а результаты сохраняют в `artifacts/`, который исключён из Git. Сохраните на принимающей машине `verification.json`, журналы заданий и версии Windows, Corel и SDK. Результаты с предыдущего компьютера описаны в [отчёте](verification-hybrid-0.4.0.md); сами артефакты через Git не передаются.

Соблюдайте `AGENTS.md`: не изменяйте чужие документы и GMS, не вызывайте `Application.Quit`, не повторяйте автоматически операцию с неизвестным исходом. При частичной или неизвестной мутации сохраните диагностику и сначала установите фактическое состояние документа. Проверка в новом процессе Corel и подтверждение прав на interop остаются отдельными условиями приёмки.
