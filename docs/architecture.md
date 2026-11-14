# Архитектура

TypeScript MCP → CSharpExecutor → JSON Lines → C# STA/message loop → CorelDRAW 27.
Python host и React UI используют тот же каталог готовых операций и не вызывают COM.
Версия 0.3.0 не имеет PowerShell COM/VBA backend или активного observer-процесса.

## Выполнение

Node ведёт request_id, Job, deadline, reports, артефакты и исходную вычислительную геометрию.
CSharpExecutor запускает один EXE с shell:false, проверяет version/protocol/runtime/x64 handshake,
принимает один pending job, ограничивает UTF-8 JSONL запись 4 МиБ и не перезапускает worker после
потери результата. Старый/несовместимый handshake отклоняется до dispatch.

Worker читает stdin отдельно от COM. Все нативные вызовы выполняются синхронно на одном STA
с Windows Forms message loop под Local\CorelDrawMcp_v27. Dispatch guard остаётся занят и при
повторном входе message loop, и до завершения cleanup. Закрытие stdin дожидается принятой работы;
COM не прерывается deadline, CorelDRAW не завершается. Второй job получает BUSY.

Connection привязан к потоку и точной версии 27, не выполняет reconnect/replay. Registry использует
IUnknown документа; page/layer native TreeNode.Handle остаётся внутри worker, наружу непрозрачные
session IDs. Фигуры адресуются StaticID. COM leases дедуплицируются; FinalRelease/GC не используются.

StateScope сохраняет Unit/refpoint/preserve-selection, active doc/page, optimization/events и живые ID
выделения. Cleanup каждого ресурса независим, исходная ошибка не теряется. Успешно открытый undo group
закрывается в finally; automatic Undo нет. Begin сам может изменить Dirty, поэтому applied не выводится
из отсутствия созданных фигур. Dirty исходника не сбрасывается для маскировки изменения.

## Операции и геометрия

Strict Zod schemas, JSON Schema variants и типизированные STJ DTO задают явные операции. Все входы и
цели проверяются до Begin; нет компиляции кода модели или универсального COM вызова по имени.
Drawing journal возвращает observed affected/created/deleted IDs и фактические параметры; большие DTO
уходят в bounded JSON artifact. Partial result также сохраняет известные ID.

Read-only измерение/экспорт и текстовый geometry fingerprint используют собственный CreateDocumentFrom
clone после проверки ID/type/physical geometry. Исходный текст не конвертируется. TypeScript сохраняет
de Casteljau flattening, convex hull и rotating calipers: boundary_error=tolerance, dimension_error≤2*tolerance,
предел 200000 точек. API metadata переносит прежний ITypeInfo алгоритм, не вызывает arbitrary getter/Invoke.

Export использует staging рядом с назначением и публикуется только после Finish. PDF настройки
восстанавливаются; SaveAs остаётся нативным и не обещает атомарности. Fixed export property bindings
проверяют ITypeInfo GUID/DispID; пользователь не задаёт имя COM-свойства.

Import: local file identity/size/SHA256 → frozen artifact → безопасный SVG-разбор или одна PDF-страница
через pinned PDFsharp 6.2.4 MIT → private Corel document → подготовленный собственный CDR → transfer layer
с job provenance markers → batch MoveToLayer. Clipboard и cross-document-copy fallback отсутствуют.
Временные неизвестные/nonempty объекты не удаляются; неподтверждённый cleanup сохраняет их точный ID.

Layout revision включает точную binary64 геометрию, матрицы, ID, страницу и параметры; текстовые glyphs
читаются в собственном clone. Dry_run не открывает undo group; apply требует актуальный revision и отсутствие
unplaced. Shelf не поворачивает/масштабирует/копирует. PowerClip имеет отдельный bounded walker для bitmap,
групп и nested clips; extract снимает один уровень без изменения размера содержимого.

Bitmap и смешанные группы принимаются transform/duplicate/group/move-to-layer и align/grid/layout.
Revision bitmap использует полный CropEnvelope и матрицу; прямоугольный bitmap без crop — фактический frame.
Boolean/convert/style не получают bitmap. Import diagnostics возвращает наблюдаемые шрифты/effects,
но font/effect/source fidelity остаются unknown; доступность семейства в Windows не доказывает отсутствие подмены.

## Host, история и обновление

Policy v3 пересекает явную capability table с MCP-каталогом. Системные поля скрыты во всех корневых
JSON Schema alternatives. Frozen document, operation/scope digest, Stop, owner identity и бюджет postchecks
повторно проверяются перед dispatch. После drawing резервируются inspect/preview той же страницы.
Revision не генерируется host. Import fingerprint фиксируется host и recheck выполняется до отправки.

CSRF/bootstrap, Credential Manager, provider contract, secret redaction и существующая access policy
сохранены. Technical log имеет allowlist и не получает paths/arguments. Исторические v1/v2 DTO, observer
summary и retained-GMS proof читаются как данные. Старые tool names не регистрируются и не dispatch-ятся.
Unknown/cleanup barriers не снимаются пользовательским boolean и не обходятся обновлением.

Read-only pre-upgrade проверяет owner markers, native reports и host history. SQLite читается immutable RO
только без незавершённого WAL/journal; reports/history не меняются. Разрешение старого retained GMS признаётся
только при совпадении committed absence evidence с исходным отчётом. Активный owner или неполная проверка
блокируют активацию. Self-contained публикация отдельно заблокирована до подтверждения прав на interop.

### Подготовка гибридного runtime

План: `.kilo/plans/2026-11-13-corel-hybrid-runtime.md`. `HybridPolicy` выбирает маршрут до мутации по проверенным целям и порогу20. В явном `execution_mode=hybrid` готовый GMS выполняет create/update batch, крупные create/style/move/duplicate и текстовые пакеты. Неподдержанные структуры прежних инструментов получают C#-маршрут до первой мутации. Новые batch-команды требуют поддержанного native runtime; ошибки не переключают backend.

Manifest связывает protocol/version/Corel27/sourceSHA/GMS SHA и фиксированные capability IDs. Runtime держит read leases, сверяет точные имя/путь загруженного проекта и nonce. VBE/Compile находятся только в developer build tool. Общий модуль не выгружается при завершении другого клиента. Потеря ответа блокирует новые мутации текущего worker; чтение и диагностика остаются доступны, replay запрещён.

Геометрия извлекается в подтверждённом private document; C# проверяет bounded JSON и публикует UTF-8 `.part`→`.json`. Private COM-ссылки освобождаются перед Close. Математика остаётся в TypeScript. Нулевые layout bounds, противоречащие ненулевой геометрии, дают `UNSUPPORTED_LAYOUT_BOUNDS`; Printable исходника не переключается.

Corel WideText удаляет LF, сохраняя CR; postcheck учитывает наблюдаемое хранение CRLF/CR. Создание, замена диапазонов, установление текста и формат проверяются по фактическому содержимому/шрифту. SVG с распознанным встроенным bitmap получает `SVG_EMBEDDED_RASTER`; отсутствие такого bitmap не доказывает полноту векторного сохранения.

Default остаётся `csharp` до итоговых performance/safety/fresh-Corel gates. Упаковка разрешает только собственный GMS по фиксированному пути и хешу, отвергает VBE dependency/лишние GMS и сохраняет gate прав interop.
