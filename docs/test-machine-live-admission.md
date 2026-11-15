# Явный live-допуск тестового стенда

`scripts/check-live-admission.mjs` — отдельный диагностический CLI для тестирования собственных временных документов после явного принятия оператором старой неопределённости. Он **не** используется установщиком, не изменяет `check-preupgrade.mjs`, не разрешает установку и не переписывает старые reports/SQLite/applied.

Создание нового receipt после явного согласия оператора:

```powershell
node scripts/check-live-admission.mjs --accept-historical artifacts/full-live-gate/<run-id>/live-acceptance.json
```

Перед каждым новым блоком, без автоматического повторного принятия изменённого снимка:

```powershell
node scripts/check-live-admission.mjs --receipt artifacts/full-live-gate/<run-id>/live-acceptance.json
```

Родительский каталог должен уже существовать. Выходной receipt создаётся эксклюзивно: существующий файл не заменяется. Цель всегда `owned-fixture-live-tests`. CLI не принимает произвольные history/report roots; используется тот же набор стандартных roots, что у pre-upgrade, плюс явный `CORELDRAW_MCP_RUNS`, если он задан. Смена roots инвалидирует receipt.

Snapshot содержит SHA-256 оригинальных reports/SQLite, состав каталогов, перечень принятых исторических исключений и число проверенных владельцев. SHA-256 — commitment к содержимому, **не цифровая подпись и не доказательство исхода мутации**. Пользователь с правом записи может заменить receipt; это локальная запись явного решения оператора, а не граница между недоверенными пользователями. Не передавать её как доказательство `applied=false`.

Принимаются только старые terminal succeeded/failed reports с подтверждённым завершением cleanup; retained project явно остаётся исключением. История с `MUTATION_OUTCOME_UNRESOLVED` может быть принята только после строгой проверки структуры/readiness самим существующим history reader. Running/unknown/timed_out, uncheckpointed journals, cleanup failure, неподтверждённый/live owner, malformed/symlink evidence и изменившиеся bytes/каталоги блокируются. Владельцы проверяются повторно после сканирования.

Receipt не заменяет свежий Corel baseline и exact GMS inventory, не разрешает менять пользовательские документы, автоматически выгружать чужой GMS, убивать worker, повторять unknown или делать Undo. Новый live-блок пишет собственные артефакты; unexpected unknown/partial и cleanup failure останавливают дальнейшие мутации до установления фактического исхода.

Принятие исторической неопределённости не называется восстановлением истории. В итоговой матрице всегда отдельно указываются: строгий historical/pre-upgrade admission, новый fixture live-допуск, фактические функциональные результаты и performance.

## Пилот И Маршрутизация

Текущий default — `csharp`. Для opt-in тестов устанавливать `CORELDRAW_MCP_EXECUTION_MODE=hybrid` только в запускаемом процессе; это тот же C# worker, а не новый backend. После успешной приёмки и пилота отдельно рассмотреть hybrid default; чистый C# остаётся диагностическим режимом.

| Операция в режиме hybrid | Существующий маршрут `hybrid-v1` |
| --- | --- |
| `corel_create_batch`, `corel_update_batch` | Native VBA, от 1 до 1000 допустимых элементов; в чистом C# — HYBRID_REQUIRED |
| `corel_create_shapes` | Native при 20+ элементах, иначе C# |
| Move/style | Native при 20+ поддерживаемых целях после полной проверки, иначе C# |
| Duplicate | Native при 20+ создаваемых копиях поддерживаемых целей, максимум 1000 |
| Set/format/replace text, text-to-curves | Native для допустимого пакета 20+ текстовых целей; дополнительные Unicode/размер/type проверки могут исключить native до dispatch |
| Геометрический снимок/измерение | Native extractor в hybrid, последующая geometry обработка остаётся C# и TypeScript |
| PNG/export/preview/inspection и остальные операции | Существующий C# маршрут, ускорение не обещается |

Выбор C# **до native dispatch** для неподдерживаемого случая — политика маршрутизации, а не повтор ошибки. После native dispatch failure/unknown переход на C# и replay запрещены.

Пример отдельного тестового запуска из корня исходников, без изменения установленного host:

```powershell
$previous = $env:CORELDRAW_MCP_EXECUTION_MODE
$previousWorker = $env:CORELDRAW_MCP_CSHARP_WORKER
try {
    $env:CORELDRAW_MCP_EXECUTION_MODE = 'hybrid' # Для отдельной диагностики: 'csharp'.
    $env:CORELDRAW_MCP_CSHARP_WORKER = (Resolve-Path 'worker/CorelMcp.Worker/bin/Release/net10.0-windows/CorelMcp.Worker.exe').Path
    node dist/index.js
} finally {
    $env:CORELDRAW_MCP_EXECUTION_MODE = $previous
    $env:CORELDRAW_MCP_CSHARP_WORKER = $previousWorker
}
```

Это stdio MCP: клиент завершает ввод только после terminal результата и cleanup, stdout не используется для произвольных логов. В реальном harness переменные задаются в env отдельного child-процесса, не в системном окружении. Пример не заменяет admission, exact GMS identity, собственный каталог runs и подтверждённые права на эффекты документов. Для установленной панели применяется штатный launcher; этот план не меняет его config и не устанавливает пакет.

Выход из пилота: прекратить новые команды, дождаться всех terminal outcomes/cleanup, закрыть только свой MCP; следующий запуск явно `csharp`. Это смена будущего исполнения, не откат уже выполненных мутаций.

Для .NET live functional suites при уже загруженном проверенном worker-output GMS допускается **только тестовый** `CORELDRAW_TEST_NATIVE_DIRECTORY=<absolute worker-output native path>`. DrawingClient и fault-injection wrapper используют этот явный root; manifest/name/hash/path проверки NativeVbaRuntime остаются прежними. Test teardown выгружает исключительно собственный test-output runtime, не shared worker-output. Тест sealed-module load/dispose lifecycle, рассчитанный на иной путь, не запускается в таком instance и не считается пройденным. Это не настройка production MCP и не fallback при отказе.
