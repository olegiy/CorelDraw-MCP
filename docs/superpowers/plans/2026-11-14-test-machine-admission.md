# Допуск нового live-контекста тестового стенда

> Исполнитель: `executing-plans`, `test-driven-development`, `verification-before-completion`. Без новых Agent Manager sessions, коммитов и установки.

**Основание:** пользователь явно разрешил изменение admission и мутации, затем подтвердил, что компьютер тестовый и дополнительных ограничений не требует. Это разрешение на тестирование, не доказательство исторического applied=false.

**Цель:** продолжить live gate без подделки старых исходов и без ослабления pre-upgrade установщика.

**Архитектура:** отдельный CLI `scripts/check-live-admission.mjs` создаёт эксклюзивный JSON receipt принятия точного исторического снимка. Purpose всегда `owned-fixture-live-tests`; receipt связывает стандартные roots, все report bytes, history bytes, состав roots и причины исторических исключений. Перед каждым новым live-блоком повторная проверка owners и снимка. Строгий `check-preupgrade.mjs` не меняется. Нет записи в исходную историю, скрытого env override или разрешения installer.

**Стек:** Node.js 24, существующий history reader Python 3.12, Windows process checker; без зависимостей.

## Границы

- Только terminal succeeded/failed reports с completed cleanup/errors=[]; retained projects остаются явно принятым историческим риском, не считаются выгруженными.
- История допускается только если строгий reader вернул ok либо ровно MUTATION_OUTCOME_UNRESOLVED после проверки schema/integrity/readiness. Unknown/running/timed_out, незавершённые Run, journals, active owner, malformed evidence блокируют создание и проверку receipt.
- Исторические failed applied/partial не переписываются. Их исход фиксируется как accepted historical uncertainty; это не full historical recovery PASS.
- Изменение любого связанного файла или состава стандартных roots инвалидирует receipt. Новый live-run использует отдельный каталог артефактов и собственные документы.
- GMS identity/readiness остаются отдельным обязательным gate. Receipt не разрешает автоматически выгрузить pre-existing runtime, убить worker или повторить unknown.

## Task 1: CLI и тесты

Файлы: новый `scripts/check-live-admission.mjs`, новый `test/live-admission.test.mjs`.

Интерфейс: `captureLiveAdmission({reportRoots, hostRoots, processExists, inspectHistory})` → snapshot; `verifyLiveAdmission(receipt, options)` → `{ok,reason?}`. В production CLI roots берутся только из defaultUpgradeOptions. Receipt содержит schema_version=1, purpose, accepted_at, files, directories, historical_exceptions, snapshot_sha256; digest вычисляется из canonical snapshot без volatile accepted_at.

- [ ] RED: реальные временные report/history fixtures; accepted missing applied остаётся byte-identical; changed report, added directory, live/unverified owner, unknown status, incomplete cleanup, history failure, purpose/digest tampering блокируются.
- [ ] GREEN: ограниченная read-only инвентаризация, no-links, size/count bounds, повторное чтение evidence и owners, exclusive receipt output, никаких изменений исходников reports.
- [ ] Регрессия: `node --test test/live-admission.test.mjs test/preupgrade.test.mjs`; строгий preupgrade по-прежнему отклоняет missing applied.

## Task 2: Неживой baseline

- [ ] Сборка worker закреплённым SDK; `npm test`, .NET Release без LIVE, contracts, typecheck и Ruff; сохранить коды/логи. Выполнять последовательно с COM.
- [ ] Создать receipt явным CLI `--accept-historical <new-receipt-path>`, проверить его через `--receipt <path>`; записать строгий admission отдельно как BLOCKED.

## Task 3: Live gate

- [ ] Свежий C# baseline; GMS exact identity/readiness только типизированным путём и под общим mutex. Если узкого пути нет — тест native lifecycle на новом Corel с проверенным отсутствием совпадений; не обобщать ошибку пути.
- [ ] После проверки receipt выполнить C# LiveSmoke, затем MCP integration; по каждому сбою сначала диагностика, не считать full PASS.
- [ ] Native test-output lifecycle/guards только после проверки ownership unload; затем worker-output hybrid smoke, performance отдельно. Не запускать исследовательские тесты вслепую.
- [ ] Не продолжать после нового unknown, активного worker timeout, cleanup failure или изменённого чужого документа. Сохранить evidence и актуальный отчёт.

## Review Focus

1. Receipt не используется установщиком и не превращает historical unknown в applied=false.
2. Hash/purpose/root tampering и новые reports не наследуют согласие.
3. Исторический retained flag не считается свежим inventory GMS.
4. Проверка owners повторяется после сканирования, не только до него.
5. Full PASS невозможен при старом historical recovery BLOCKED; live fixture PASS объявляется отдельно.
