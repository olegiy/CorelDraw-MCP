# Локальная сборка по handoff и продолжение live-проверок — 14 ноября 2026

## Итог

Ветка `codex-like-ui-task-1`, HEAD `5abacdb`, origin `https://github.com/olegiy/coreldraw-mcp.git`. Ветка уже была активна. Три существующих локальных диагностических отчёта не изменялись.

Interop, C# worker и основной `dist` собраны заново из текущего checkout. Установленный host, регистрация MCP, системный PATH, исходные исторические reports и SQLite не изменялись. Это development-сборка, не установка и не распространяемый пакет.

Мутационная часть handoff **не выполнена**: штатная read-only проверка `node scripts/check-preupgrade.mjs` возвращает `MUTATION_OUTCOME_UNRESOLVED`. Запуск `LiveSmoke`, `npm run test:integration` и 100 native-операций/1000 плашек в обход этого барьера не производился.

## Среда и сборка

| Компонент | Подтверждённое значение |
| --- | --- |
| Windows | Windows 11 IoT Enterprise LTSC, 10.0.26100, x64 |
| Node.js | 24.18.1 |
| .NET SDK | 10.0.404, MSBuild 18.9.15 |
| Runtime | .NET 10.0.15, Microsoft.WindowsDesktop.App 10.0.15 |
| CorelDRAW | 27.2.0.135, существующий PID 24044 |
| VGCore typelib | GUID `95e23c91-bc5a-49f3-8cd1-1fc515597048`, 27.2, SYS_WIN64 |
| Источник interop | `C:\Program Files\Corel\CorelDRAW Graphics Suite\27\Programs64\TypeLibs\VGCoreAuto.tlb` |
| SHA-256 typelib | `7044b6074dfa445f39f6238062ae40e96e01c7764dc28d215872abacce3bdd7c` |
| SHA-256 generated interop | `e63a36177756e8e6c92d05aa677cb43376bd97aaf2d5cbe2e7eff828413fc0fa` |

Системный `dotnet` содержит только SDK 8.0.428. Использован уже существовавший локальный SDK:

```powershell
$dotnetExecutable = (Resolve-Path '.kilo\worktrees\codex-like-ui-task-1\.superpowers\sdd\1794339253999-csharp-worker-typed-corel-tools\dotnet\dotnet.exe').Path
$env:DOTNET_ROOT_X64 = Split-Path -Parent $dotnetExecutable
powershell.exe -NoProfile -File scripts/build-worker.ps1 -DotnetPath $dotnetExecutable
npm ci
npm run build
```

Worker: `worker/CorelMcp.Worker/bin/Release/net10.0-windows/CorelMcp.Worker.exe`. Сборка C# завершилась без ошибок и предупреждений компилятора. Генератор interop сохранил в `worker/generated/fingerprint.json` предупреждение конвертера: `IPDFSettingsInternal` объявлен как dual, но не наследует IDispatch; импортирован как IUnknown-derived. Права на распространение interop по-прежнему не подтверждены.

При `npm ci` обнаружена унаследованная переменная `NODE_TLS_REJECT_UNAUTHORIZED=0`. Она не устанавливалась этой работой; для последующих проверок удалена из окружения дочернего процесса. Машинная конфигурация TLS не менялась.

## Проверки без мутаций

| Проверка | Результат |
| --- | --- |
| `npm test`, первый прогон | 149 passed, 1 failed, 2 skipped |
| `node --test test/worker-package.test.mjs`, после восстановления notices | 9 passed, 0 failed |
| `npm test`, полный повтор | 150 passed, 0 failed, 2 skipped |
| .NET tests Release, live-флаг отсутствует | 166 passed, 0 failed, 52 skipped |
| `npm run check:contracts` | `contracts ok` |
| `node node_modules/typescript/bin/tsc --noEmit -p tsconfig.json` | Код 0 |
| Реальный protocol test и named mutex test с новым worker | 2 passed, 0 failed |

Отдельный lint-скрипт в корневом `package.json` отсутствует. Две опциональные Node-проверки, пропущенные полным прогоном, затем выполнены отдельно: `CORELDRAW_WORKER_PROTOCOL_TEST=1`, `DOTNET_HOST_PATH` указывает на локальный SDK, `CORELDRAW_MCP_CSHARP_WORKER` — на новый worker. Mutex-проверка выполнена последовательно, без других COM-тестов.

Первый сбой `sealing binds reviewed evidence, interop bytes and pinned notices in an isolated synthetic package` вызван checkout-преобразованием LF → CRLF при `core.autocrlf=true`. Для всех шести `dotnet-*.TXT` рабочие SHA-256 не совпадали с manifest, а точные Git HEAD bytes совпадали. Восстановлены только эти шесть файлов из предварительно проверенных HEAD bytes; их содержимое равно HEAD. Добавлено правило `worker/third-party/dotnet-*.TXT -text` в `.gitattributes`, чтобы checkout больше не изменял закреплённые байты. Хеши, лицензии и проверка целостности не ослаблялись.

Полные журналы Node: `artifacts/handoff-20261114-npm-test.log` и `artifacts/handoff-20261114-npm-test-green.log`.

## Свежий живой read-only прогон

14 ноября, **17:11:30–17:11:35 UTC**, новые сервер и worker для каждого режима. Не использована старая готовая worker-сборка из другого worktree.

### C#

Каталог tools содержит 39 typed tools, без `corel_execute_vba`. Выполнены четыре успешных задания с завершённым cleanup:

1. `corel_status`: CorelDRAW 27.2.0.135, worker 0.4.0, STA, `.NET 10.0.15`, режим `csharp`, ready.
2. `corel_describe_api`, members Document, limit 10.
3. `corel_describe_api`, enum cdrUnit.
4. Финальный `corel_status`.

В обоих status `documents=[]`, `active_document_id=null`, `application_state={optimization:false,events_enabled:true}`. Это текущее наблюдение, а не прежний пустой документ из предыдущего отчёта. Документы не создавались, поэтому инспекция содержимого, preview, редактирование и сохранение в этом прогоне не проверены.

### Hybrid

Первый `corel_status`, job `a3f741c8-d57d-4326-88b6-fa0af23303dd`, завершился подтверждённым отказом:

```text
status: failed
stage: native_readiness
error.code: COM_ERROR
error.message: Обнаружен другой VBA-проект с именем runtime; вызов запрещён.
error.applied: false
cleanup.completed: true
cleanup.errors: []
```

Условие отказа находится в `worker/CorelMcp.Worker/Hybrid/NativeVbaRuntime.cs:94`: обнаружен дубликат имени либо путь загруженного проекта не совпадает с точным ожидаемым путём нового runtime. Конкретный путь загруженного проекта данным ответом не раскрыт, поэтому не объявляется установленным фактом. Это не неизвестный исход batch-мутации: batch не запускался.

SHA-256 GMS в исходном `worker/native`, новом bin и прежнем bin одинаковый: `126b41afa31e908d1b0008b87cfb18812ade20502fbb52408837b9ea5654d0f9`. Одинаковые байты не отменяют обязательную проверку точного пути загруженного проекта. Защита не отключалась; GMS не выгружался, не удалялся, не компилировался. CorelDRAW не перезапускался.

Доказательства:

```text
artifacts/handoff-live-20261114/319da2ae-fb04-4084-bf3f-00f6732fcc8c/verification.json
artifacts/handoff-live-20261114/319da2ae-fb04-4084-bf3f-00f6732fcc8c/csharp/verification.json
artifacts/handoff-live-20261114/319da2ae-fb04-4084-bf3f-00f6732fcc8c/hybrid/verification.json
```

В подкаталогах `runs` сохранены исходные `report.json` и `run.log` каждого нового задания. Эти артефакты исключены из Git. В C#-проверке сохранено сравнение метаданных документов; application_state дополнительно сверено по двум сохранённым status. Диагностический сценарий `artifacts/handoff-live-20261114.mjs` после прогона уточнён для прямого сравнения вложенного `application_state`; повтор hybrid-отказа не выполнялся.

## Оставшиеся условия handoff

1. Штатно разрешить исторический `MUTATION_OUTCOME_UNRESOLVED` без переписывания reports и replay; затем получить успешный pre-upgrade gate.
2. Установить точную идентичность уже загруженного GMS и безопасный переход к runtime новой сборки. Не удалять общий установленный runtime как временный проект. Проверка в новом процессе Corel остаётся отдельным условием; автоматического перезапуска не было.
3. После снятия барьеров выполнить последовательно LiveSmoke, `npm run test:integration`, затем отдельно `node test/integration-hybrid.mjs`, только на собственных временных документах.

Коммиты, push, установка и публикация не выполнялись. Полный live gate не объявляется пройденным.
