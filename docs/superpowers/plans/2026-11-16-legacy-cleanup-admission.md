# Legacy-cleanup admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pre-upgrade verifier принимает доказанно чистый cleanup ранних legacy-отчётов (`completed:true` без ключа `errors`), не переписывая отчёты и не ослабляя текущий формат.

**Architecture:** Единственная точка изменения — `scripts/check-preupgrade.mjs:94`. Признак legacy-формата: `cleanup.completed === true` и полное отсутствие ключа `errors`. Это безопасно, потому что ранний PowerShell-исполнитель (git `01ebda6`) ставил `completed=$true` при инициализации и переводил в `$false` при любом сбое cleanup (ошибки шли в warnings, массив `errors` не писался); более поздние legacy-версии (`cfc8347`, `0753fa2`) и текущий C#/TS всегда пишут `errors:[]`. Отсутствие ключа однозначно идентифицирует ранний контракт, где `completed:true` ⇔ все шаги cleanup успешны.

**Tech Stack:** Node 24, `node:test`, существующий `scripts/check-preupgrade.mjs`.

**Spec:** этот план (root-cause ниже).

## Root cause (evidence)

- `git show 01ebda6:powershell/CorelAutomation.psm1`: `$script:Cleanup = @{ completed = $true }`; каждый сбой cleanup → `completed=$false` + warning; ключ `errors` не создаётся.
- `git show cfc8347` / `0753fa2`: `Add-CleanupError` и инициализация `errors=@()` — ключ `errors` присутствует всегда.
- `worker/CorelMcp.Worker/WorkerSession.cs:104`: `Snapshot()` всегда содержит `errors`; `src/jobs.ts:93`: local cleanup `{completed:true, errors:[]}`.
- Read-only скан `%LOCALAPPDATA%\CorelDrawMcp\runs`: 177/177 отчётов имеют `cleanup:{completed:true}` без `errors` → ранний legacy-контракт.

## Global Constraints

- Исходные reports/SQLite/Run outcomes не переписывать (AGENTS.md).
- Не ослаблять gates: `completed:false`, `errors` не-массив, непустой `errors`, retained GMS, unknown/partial mutation — по-прежнему отказ.
- Не запускать live COM; проверка только read-only.
- Scope: только false-positive `CLEANUP_NOT_PROVEN` для legacy-формата. Последующие законные блокировки (`MUTATION_OUTCOME_UNRESOLVED` для 34 failed без `error.applied=false`, `RETAINED_PROJECT_UNRESOLVED` для 32 retained) — отдельные gates, вне этого исправления.

## Review Focus

- Legacy `completed:true` без `errors` → принимается, bytes отчёта неизменны.
- Legacy `completed:false` без `errors` → отказ `CLEANUP_NOT_PROVEN`.
- `errors` присутствует, но не массив / непустой → отказ.
- Текущий формат `errors:[]` → поведение не меняется.
- Retained GMS и unknown mutation не обходятся новым правилом.

---

### Task 1: Legacy-cleanup acceptance в verifier

**Files:**
- Modify: `scripts/check-preupgrade.mjs:94`
- Test: `test/preupgrade.test.mjs`

**Interfaces:**
- Consumes: `checkUpgrade(options)` (существующий экспорт).
- Produces: то же поведение `checkUpgrade`, плюс принятие legacy-формата cleanup.

- [ ] **Step 1: RED — добавить тесты**

В `test/preupgrade.test.mjs` добавить:

```js
test('legacy cleanup completed:true without errors key is proven clean and preserved', () => {
  const f = fixture('succeeded', { cleanup: { completed: true } });
  const before = readFileSync(f.path);
  assert.equal(checkUpgrade(f.options).ok, true);
  assert.deepEqual(readFileSync(f.path), before);
});
test('legacy cleanup completed:false without errors key still refuses', () => {
  const f = fixture('succeeded', { cleanup: { completed: false } });
  assert.equal(checkUpgrade(f.options).ok, false);
});
test('cleanup errors present but malformed or non-empty still refuses', () => {
  for (const errors of [null, 'x', {}, [{ step: 's', code: 'CLEANUP_ERROR' }]]) {
    const f = fixture('succeeded', { cleanup: { completed: true, errors } });
    assert.equal(checkUpgrade(f.options).ok, false, JSON.stringify(errors));
  }
});
test('legacy rule does not bypass retained GMS or unknown mutation', () => {
  const retained = fixture('succeeded', { cleanup: { completed: true, project_retained: true } });
  assert.equal(checkUpgrade(retained.options).ok, false);
  const failed = fixture('failed', { cleanup: { completed: true } });
  assert.equal(checkUpgrade(failed.options).ok, false);
});
```

- [ ] **Step 2: Запустить RED**

Run: `node --test test/preupgrade.test.mjs`
Expected: FAIL на первом тесте (`ok:false` вместо `true`), остальные новые тесты PASS.

- [ ] **Step 3: GREEN — минимальное изменение**

В `scripts/check-preupgrade.mjs` заменить строку 94:

```js
const cleanup = report.cleanup;
const legacyClean = cleanup?.completed === true && !Object.hasOwn(cleanup, 'errors');
const currentClean = cleanup?.completed === true && Array.isArray(cleanup.errors) && cleanup.errors.length === 0;
if (!legacyClean && !currentClean) fail('CLEANUP_NOT_PROVEN');
```

(сохранить последующую проверку `project_retained` без изменений).

- [ ] **Step 4: Запустить GREEN**

Run: `node --test test/preupgrade.test.mjs`
Expected: PASS все тесты файла.

- [ ] **Step 5: Полный suite**

Run: `npm test`
Expected: 0 failed (live-тесты skipped без env).

- [ ] **Step 6: Read-only повтор на реальных данных**

Run: `node scripts/check-preupgrade.mjs`
Expected: `CLEANUP_NOT_PROVEN` больше не первая причина; ожидается следующая законная блокировка (`MUTATION_OUTCOME_UNRESOLVED` / `RETAINED_PROJECT_UNRESOLVED`) — зафиксировать фактический reason, не объявлять admission пройденным.