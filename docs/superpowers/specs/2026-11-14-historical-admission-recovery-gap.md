# Пробел recovery для исторического admission

**Статус:** проект отдельного решения для согласования; реализация не разрешена и не начата. Этот документ не снимает admission и не подтверждает исход старых мутаций.

**Дополнение после решения пользователя 14 ноября:** пользователь отдельно разрешил менять admission и выполнять мутации на тестовом компьютере. Вместо реализации доказательного resolution выбран более узкий `owned-fixture-live-tests` receipt принятия исторической неопределённости: `docs/superpowers/plans/2026-11-14-test-machine-admission.md`. Это не согласование `unapplied_proven`, не снятие строгого pre-upgrade и не разрешение установки. Нижеприведённый проект квалификации исторических исходов остаётся нереализованным.

**Основание:** этап 2 плана `docs/superpowers/plans/2026-11-14-full-live-gate.md`, согласованного для ветки `14-11-2026`, HEAD `1cceb0b367bbf2e310c31a08ee32b954bfcfaa36`.

## Свежие наблюдения

Доказательства: `artifacts/full-live-gate/20261114T175719Z/`.

- В checkpointed оригинальной истории 25 Run, 43 операции, 9 terminal failed. У всех девяти `OperationResult.applied=null`, а в error исходного report поля `applied` нет.
- В стандартном CorelDrawMcp runs root 83 terminal reports. 11 failed reports не удовлетворяют admission; 9 связаны с историей, ещё 2 legacy VBA reports в ней не найдены.
- Три reports содержат `project_retained=true`. Для `CorelMcp_dc3c5d1173cb` история уже содержит валидное подтверждение отсутствия проекта. Это освобождает только cleanup-обязательство, не доказывает неприменение VBA.
- `runtime.json` и `mcp-owner.json` в host root отсутствуют, `recovery_pending=[]`, незавершённых Run нет. Все 24 owner PID из оригинальных reports проверены через Windows process API: ошибка 87, PID отсутствует. Историческое creation_time не восстановлено и не выдумывается.
- Основной admission и admission диагностической копии возвращают `MUTATION_OUTCOME_UNRESOLVED`. Проверка Store readiness сама возвращает `reason=null`; ошибка возникает на дополнительном требовании pre-upgrade к failed applied.
- Все 43 ledger reports отличаются от disk report только дополнительными полями `log_tail` и `log_truncated` в ledger. Это обогащение чтения отчёта, не противоречие outcome. Исходные bytes/hash сохраняются отдельно; нельзя сравнивать хеши этих разных представлений как доказательство подмены.

## Почему существующих действий недостаточно

1. `webhost/corel_agent/recovery.py:40` переносит только действительно записанный `applied`. При отсутствии поля остаётся `None`.
2. `webhost/corel_agent/preupgrade.py:63` требует `applied=false` и в OperationResult, и в error report. `scripts/check-preupgrade.mjs:93` независимо предъявляет аналогичное требование disk reports, включая reports вне SQLite.
3. В `webhost/corel_agent/app.py:308` reconcile допускается только в recovery-only режиме при существующем owner marker либо pending binding. Здесь нет ни marker, ни pending; искусственно создавать их нельзя.
4. Review API теперь только читает сохранённые записи. POST `/recovery/review` отсутствует; тест `webhost/tests/test_recovery_api.py:11` запрещает превращать review в разрешение исполнения.
5. Пустой список документов в новом Corel и отсутствие старого GMS не доказывают, что историческая мутация не выполнялась. `STALE_DOCUMENT`, `COMPILATION_FAILED` или имя read-only tool сами по себе также недостаточны без доказанного контракта той версии и стадии выполнения.

## Варианты

### A. Сохранить текущий fail-closed контракт

Не менять production, оставить исторические unknown неизменными и остановить новый live gate. Это единственный уже реализованный безопасный вариант. Повтор restart/reconcile, очистка БД или тестовый root не заменяют admission.

### B. Отдельный доказательный журнал resolution

Рекомендуемое направление **только для проектирования и согласования**, если имеются независимые доказательства, достаточные для конкретных операций. Простой операторский флаг, согласие на риск или «новый контекст» не считаются таким доказательством. Не обещается, что решение разрешит все 11 текущих reports.

Журнал должен хранить новые решения отдельно от исходных reports, OperationResult и outcome Run. Модель resolution различает `unapplied_proven`, `applied_verified`, `partial_verified`, `unresolved`; это новая квалификация доказательств, не переписывание исторического `applied`.

### Требования к проекту варианта B

- Фиксированные стандартные roots; caller не выбирает произвольную БД или путь отчёта. Проверки SQLite и всех disk reports используют один подтверждённый verifier, включая reports без ledger binding.
- Обязательная связь с исходным SHA-256 report, job/request/tool/owner_session, operation/run при их наличии, версией legacy исполнителя и набором hash-bound дополнительных доказательств. `log_tail` не участвует в идентичности оригинального report.
- Append-only решения с schema/version/revision, отдельным временем проверки, verifier identity и проверяемым commitment. Копирование, смена root, stale evidence, конфликт revision, изменение оригинала, пропавшее доказательство и неизвестная версия — fail-closed.
- Для `unapplied_proven` необходим независимый проверенный stage/dispatch witness конкретного legacy исполнителя. Не делать вывод по тексту ошибки, имени инструмента или отсутствию фигур сегодня. Legacy VBA не исполнять и не компилировать для получения witness.
- Для `applied_verified`/`partial_verified` необходимы точная идентичность исходной цели, подтверждённый конечный результат и принятые оператором остаточные изменения. Исходный outcome остаётся историческим. Возможность admission при таких квалификациях требует отдельного явного изменения контракта и тестов; текущий контракт их не принимает.
- Cleanup проверяется независимо: свежий типизированный inventory с exact name/path/dirty и GMS/VBE coverage, связи с конкретным retained project и владельцем. Никаких универсальных COM/VBA selector, first-match или автоматической выгрузки.
- Отсутствие живого исполнителя и pending работы доказывается независимо. Новая qualification не даёт права освобождать активный COM слот, replay, Undo, kill, перезапуск или изменение документов.
- Журнал не должен содержать secrets, prompts или исходный VBA; ссылки на приватные доказательства остаются локальными. Доступ — через существующие CSRF/ownership safeguards.
- Если исторический witness/точная цель недоступны, оставить `unresolved`. Никакого blanket resolve для всех failed или ручного `applied=false`.

## Минимальные обязательные тесты будущего решения

1. Текущие 11 reports без достаточных дополнительных доказательств по-прежнему блокируют admission.
2. `reconcile` сохраняет `None`, если original applied отсутствует; outcome старого Run и bytes report неизменны.
3. Валидный witness подтверждённого отказа до dispatch разрешает ровно связанное obligation; ни соседний job, ни legacy tool с тем же именем не наследуют решение.
4. Подтверждение отсутствия retained GMS не разрешает mutation obligation; orphan report вне SQLite также проверяется.
5. Changed hash, неверный job/request/owner_session, path traversal, symlink, live owner, missing witness, duplicate/contradictory/stale resolution — отказ до эффектов.
6. Crash между записью qualification и её publication не создаёт разрешения; повтор записи не дублирует действие.
7. Main admission и диагностическая копия дают согласованную классификацию без исключения стандартных roots; UI не приобретает скрытых override-параметров.

## Условие продолжения

До реализации необходимы согласование конкретной модели authority/допустимых qualification и отдельный implementation plan с RED/GREEN. До успешного admission на всех стандартных roots этапы GMS lifecycle, мутационные live suites и performance остаются заблокированы. Выбор варианта B сам по себе не доказывает, что нужные исторические witnesses существуют.
