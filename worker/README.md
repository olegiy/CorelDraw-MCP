# C#-исполнитель: разработка миграции

Целевая платформа: Windows x64, .NET SDK **10.0.404**, CorelDRAW **27**.
JSONL-протокол и каталог подключены; C# — единственный процесс-исполнитель исходников 0.4.0. В явном режиме `hybrid` он вызывает фиксированный готовый VBA/GMS для проверенных пакетов.
Выпуск распространяемого пакета заблокирован до подтверждения прав на interop;
см. [INTEROP-DISTRIBUTION.md](INTEROP-DISTRIBUTION.md). Исторический технический
отчёт этапа1 не является подтверждением готовности поставки.

Сборка из исходников требует официально установленной type library VGCore 27.
Скрипт читает 64-разрядный реестр, проверяет GUID, версию и `SYS_WIN64`, затем
генерирует типизированную interop-сборку штатным `TypeLibConverter`.
Windows PowerShell 5.1/.NET Framework используется только при **сборке**.
Исполняемый процесс работает на .NET 10. В runtime нет обращения к VBE/Compile; загрузка и вызовы готового GMS разрешены только в режиме `hybrid`.

```powershell
$dotnetExecutable = '<полный путь к dotnet.exe из SDK 10.0.404>'
powershell -NoProfile -File scripts/build-worker.ps1 -DotnetPath $dotnetExecutable
& $dotnetExecutable test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj -c Release
```

`worker/generated/fingerprint.json` содержит SHA-256 исходной type library,
архитектуру и предупреждения импортёра. Сгенерированные DLL и результаты
сборки исключены из Git. Право распространения interop **не подтверждено**;
поставка готового пакета остаётся заблокированной.

Live smoke запускается отдельно и создаёт только два временных документа:

```powershell
$env:CORELDRAW_WORKER_LIVE = '1'
$env:DOTNET_HOST_PATH = $dotnetExecutable
& $dotnetExecutable test worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj -c Release --filter FullyQualifiedName~LiveSmoke
```

Отчёты и PNG сохраняются в `artifacts/worker-smoke/<идентификатор>/` даже при
ошибке. Таймаут не завершает CorelDRAW и не повторяет операцию; потребуется
ручная проверка указанного PID и отчёта. Доступ к COM защищён прежним mutex
`Local\CorelDrawMcp_v27`, все вызовы и cleanup выполняются на одном STA-потоке
с Windows Forms message loop.

Технический live gate этапа 1 пройден с согласованным ограничением текста:
non-BMP (включая emoji), одиночные суррогаты и NUL отклоняются до мутации;
символы не заменяются молча. Проверены два временных документа, BMP Unicode,
PNG, StaticID, cleanup и сохранность контрольного документа. В начале
проверки пользовательских документов не было; непустое пользовательское
окружение требует отдельной проверки. История диагностики сохранена в
[STAGE1-REPORT.md](STAGE1-REPORT.md), сигнатуры — в [api-contract.json](api-contract.json).

Для явного development backend задайте перед запуском MCP:

```powershell
$env:CORELDRAW_MCP_BACKEND = 'csharp'
$env:CORELDRAW_MCP_CSHARP_WORKER = '<абсолютный путь к CorelMcp.Worker.exe>'
```

Backend фиксируется на процесс, автоматического fallback нет. Прежний backend отклоняется без автоматического fallback. Для непакетированного
EXE при локальном SDK нужен `DOTNET_ROOT_X64`, указывающий на каталог SDK.
Рабочий каталог запросов, request_id и polling принадлежат TypeScript;
истечение deadline не прерывает COM и не освобождает занятый слот.

Сервер публикует typed-инструменты рисования и производства
из `src/drawing-tools.ts`; execute/build VBA в этом backend отсутствуют.
Новые мутации требуют request_id, document_id и page_index, точные StaticID
и layer_id при создании. Координаты — мм от нижнего левого угла страницы,
независимо от Unit, DrawingOrigin и ReferencePoint. Неверная цель, locked
layer, отсутствующий шрифт или превышение лимита отклоняются до undo group.
Частичный сбой возвращает наблюдаемые ID и `partial:true`; автоматического
undo/replay нет. `is_special_layer` в инспекции отличает направляющие.

`test/integration-drawing.mjs` проверяет новый каталог через настоящий MCP
и worker при явных `CORELDRAW_WORKER_LIVE=1`, `CORELDRAW_MCP_BACKEND=csharp`
и `CORELDRAW_MCP_CSHARP_WORKER`. Все документы создаются тестом и закрываются
в finally, отчёт сохраняется в `artifacts/integration-drawing/`.

`test/integration-production.mjs` проверяет inspect/create/edit curve,
stale revision, буквальные find/replace и явную цепочку preflight ->
edit/style -> preflight. Отчёт — `artifacts/integration-production/`.
Кривые ограничены 10000 узлами; revision учитывает точные binary64,
порядок/типы узлов, controls, closed и матрицу. Пагинация его не меняет.
Preflight возвращает unknown для неподдержанной/неполной проверки и не
обещает пригодность станку.

`test/integration-import.mjs` проверяет SVG/PDF/PNG/JPEG, страницы и PowerClip
через MCP; диагностика сохраняется в `artifacts/import/`. Импорт требует
локальный файл, точные SHA-256/размер, слой и позицию в мм. Внешние SVG
resources, активное содержимое, неподдерживаемый текст, PDF с паролем
и отсутствующая PDF-страница отклоняются до изменения целевого документа.
Private native paths ограничены 259 символами; длинный каталог заданий
даёт явный `NATIVE_PATH_TOO_LONG`, без обхода через сторонние каталоги.
PDFsharp закреплён на MIT-версии 6.2.4; лицензия включена в выход сборки.
Перенос выполняется через собственный подготовленный CDR и job-маркеры,
без clipboard, повторной попытки другим способом и VBA. При неудачном
cleanup точный ID временного документа/слоя сохраняется в диагностике.
PowerClip принимает bitmap и вложенные контейнеры; extract снимает один
уровень, контейнер сохраняется, размеры содержимого не меняются.

Bitmap и смешанные группы поддерживаются обычными transform/duplicate/group/move-to-layer
и align/grid/layout; boolean/convert/style сохраняют векторные ограничения.
`import_diagnostics` показывает фактически наблюдаемые шрифты, их доступность в Windows
и число native effects. Соответствие шрифтов/эффектов исходнику всегда `unknown`;
фильтр мог их подменить, преобразовать или растрировать. Preview требует визуальной проверки.
