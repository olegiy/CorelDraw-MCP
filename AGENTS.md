# corel-mcp

Локальный Windows MCP-сервер и скилл для CorelDRAW **27**. Пользовательские сообщения и итоговые отчёты — по-русски.

- Исходники TypeScript в src; единственный C# .NET 10 STA worker в worker; скилл в skills/coreldraw-automation.
- Node.js 24. SDK 10.0.404 закреплён в global.json. npm test проверяет MCP/jobs; live dotnet/integration suites используют только собственные временные документы. Не запускать npm mutex test параллельно с live COM suite.
- Проверяйте подписи Context7 `/websites/community_coreldraw_sdk_api_draw`, API 27, установленной type library и live tests; фиксируйте их в worker/api-contract.json.
- Никогда не печатайте логи в server stdout. Не передавайте пользовательские аргументы через shell/Invoke-Expression. Нет произвольного кода, Roslyn, универсального COM Invoke и fallback. Гибрид допускает только фиксированный проверенный VBA runtime; VBE/Compile в runtime запрещены. До успешного native live gate активным остаётся C#-путь.
- Все COM-вызовы и cleanup сериализованы на одном STA под `Local\CorelDrawMcp_v27`. Polling не вызывает COM. Deadline не отменяет работу и не освобождает слот; неизвестную/осиротевшую/частичную мутацию автоматически не повторять и не откатывать.
- Используйте точные document_id и Shape.StaticID, непрозрачные page/layer IDs. Не выбирать по имени/первому индексу и не вызывать Application.Quit.
- До первой мутации, включая BeginCommandGroup, проверяйте все входные цели/значения. Non-BMP, lone surrogate и NUL в новом тексте запрещены; шрифты не подменять.
- Live tests сохраняют диагностику и закрывают только созданные ими документы. Пользовательские рисунки и сторонние GMS не трогать. При необходимости удалить только собственные временные diagnostic проекты CorelMcp_*; production worker не создаёт и не компилирует GMS на каждый запрос. Общий установленный runtime нельзя удалять как временную диагностику.
- Исторические DTO, unknown и retained GMS barriers сохраняются без replay и переписывания исходных reports. Pre-upgrade проверка read-only; неполная проверка запрещает активацию обновления.
- Держите схемы, скилл, JSON-рецепты, host capability table, installer и документацию согласованными. UI без отдельного запроса не переделывать.
- Выпуск распространяемого пакета заблокирован до подтверждения прав на interop; см. worker/INTEROP-DISTRIBUTION.md. Коммиты и установка в пользовательское окружение — только по отдельному разрешению.
