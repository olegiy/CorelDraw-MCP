# Incremental Donor Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Selectively add the useful functionality identified in `pefedotov/coreldraw_mcp` while preserving this project's TypeScript -> PowerShell STA -> COM/VBA execution model, durable jobs, idempotency, exact document/shape identity, and unknown-outcome rules.

**Architecture:** Implement package A as reusable VBA recipes and one bounded preview extension over the existing `corel_execute_vba` and `corel_preview` contracts; do not add six recipe-specific MCP tools. Implement package B as explicit, typed export-format capabilities. Implement package C as documentation/schema consistency checks and optional user controls/diagnostics. Every COM mutation remains a JobManager job owned by the existing worker and mutex.

**Tech Stack:** Node.js 24, TypeScript, `@modelcontextprotocol/server` 2.4.0, Zod 4.6.7, Windows PowerShell 5.1 STA, CorelDRAW 27/VBA, existing Node test runner and live integration harness.

**Spec:** `docs/donor-project-comparison.md`, especially sections 1-6 and 9; existing contracts in `docs/architecture.md` and `AGENTS.md` override donor behavior.

## Global Constraints

- Preserve `request_id` idempotency, canonical argument fingerprints, durable job reservations, and conflict rejection.
- Never automatically replay `timed_out`, `unknown`, executor-lost, or cleanup-incomplete mutations.
- Polling a job must never call COM.
- Keep all COM operations serialized under `Local\\CorelDrawMcp_v27` and the existing PowerShell STA worker.
- Use exact `document_id` and `Shape.StaticID`; do not introduce active-document or ordinal-index addressing as a replacement.
- Do not execute arbitrary expressions, `eval`, or unrestricted donor Python code.
- All temporary documents used by integration tests must be created and closed by the test; never call `Application.Quit` or modify unrelated user drawings.
- Do not add a third-party runtime dependency or change the MCP transport architecture.
- Keep stdout reserved for MCP; diagnostics go to stderr or durable job artifacts.
- Update tool schemas, skills, references, examples, and tests together; do not advertise an unverified CorelDRAW behavior.

## Review Focus

- Empty selection for `preview(target="selection")` must fail explicitly rather than silently exporting the page; test in package A.
- Selection, active page, active document, units, and undo state must be restored after successful and failed recipes/preview; test each cleanup path in package A.
- A recipe targeting a stale or foreign `document_id`/`StaticID` must never select a different object; test in package A.
- Export filters must preserve the existing destination on failure and enforce bounded raster dimensions; test each package B format.
- Documentation/schema drift and dangerous user controls must fail closed; test package C checker and config behavior.

---

### Package A: Reusable VBA Recipes and Selection Preview

#### Task A1: Define the recipe contract and shared VBA helpers

**Files:**
- Modify: `skills/coreldraw-automation/references/vba.md`
- Create: `skills/coreldraw-automation/references/recipe-contract.md`
- Create: `skills/coreldraw-automation/examples/M_CommonHelpers.bas`
- Modify: `skills/coreldraw-automation/SKILL.md`
- Test: `test/recipe-contract.test.mjs`

**Interfaces:**
- Consumes: existing `corel_execute_vba` arguments (`request_id`, `document_id`, `code`) and existing worker JSON validation.
- Produces: a documented `Main() As String` recipe contract; helper procedures for invariant JSON numbers/strings, unit conversion, explicit target lookup by `StaticID`, and structured error returns.

- [x] **Step 1: Write failing contract tests** asserting the recipe documentation identifies `Main() As String`, JSON result shape, explicit `document_id`, StaticID targeting, locale-independent numeric output, and no arbitrary expressions.
- [x] **Step 2: Run `npm test -- test/recipe-contract.test.mjs`** and confirm the new contract checks fail because the contract/helper/reference files are absent or incomplete.
- [x] **Step 3: Implement `M_CommonHelpers.bas`** with bounded, reusable helpers. Helpers must distinguish validation failure from applied-action failure, never select by ordinal index, and return JSON that the existing Node worker can parse.
- [x] **Step 4: Update skill/reference text** with the recipe invocation template, preconditions, cleanup expectations, and explicit statement that recipes are examples executed through `corel_execute_vba`, not new MCP tools.
- [x] **Step 5: Run the focused test and `npm test`**; expected result is all existing tests plus contract tests passing.

#### Task A2: Add six independently documented recipe examples

**Files:**
- Create: `skills/coreldraw-automation/examples/M_TextUnicode.bas`
- Create: `skills/coreldraw-automation/examples/M_FillOutline.bas`
- Create: `skills/coreldraw-automation/examples/M_DuplicateOffset.bas`
- Create: `skills/coreldraw-automation/examples/M_LayerGroup.bas`
- Create: `skills/coreldraw-automation/examples/M_BooleanShapes.bas`
- Create: `skills/coreldraw-automation/examples/M_CurveNodes.bas`
- Modify: `skills/coreldraw-automation/references/recipes.md`
- Modify: `skills/coreldraw-automation/references/tools.md`
- Modify: `skills/coreldraw-automation/SKILL.md`
- Test: `test/recipe-source.test.mjs`

**Interfaces:**
- Consumes: `M_CommonHelpers.bas` contract from Task A1 and existing `corel_execute_vba`.
- Produces: six self-contained examples with stable JSON result fields and explicit preconditions.

- [x] **Step 1: Write source-level tests** checking every recipe has a `Main` entry point, no `Eval`/`Execute`/unbounded file access, bounded input constants, explicit target/document handling, and documented result keys.
- [x] **Step 2: Run the focused test** and confirm it fails until all six files and references exist.
- [x] **Step 3: Implement the recipes:** Unicode artistic/paragraph text; RGB/CMYK fill and outline; duplicate with offset; create/select a named layer and group only the requested StaticIDs; Weld/Trim/Intersect with returned result StaticIDs; curve node inspection without converting source text to curves.
- [x] **Step 4: Add per-recipe preconditions and failure semantics**: empty selection, locked layer, missing StaticID, unsupported shape type, and partial mutation must be represented in JSON and must not claim rollback that was not observed.
- [x] **Step 5: Run `npm test` and PowerShell syntax checks** for every `.bas` source; expected result is no static-contract violations.

#### Task A3: Live-test the six recipes on temporary documents

**Files:**
- Modify: `test/integration.mjs`
- Create: `test/integration-recipes.mjs`
- Modify: `docs/verification.md`
- Modify: `skills/coreldraw-automation/references/recipes.md`

**Interfaces:**
- Consumes: recipe files and existing integration runner/temporary-document policy.
- Produces: machine-readable recipe verification with document/shape counts, StaticIDs, geometry, text, and cleanup state.

- [x] **Step 1: Add integration scenarios** that create a temporary document, execute each recipe through the existing MCP job path, inspect exact results, and close only the temporary document.
- [x] **Step 2: Add negative scenarios** for empty selection, stale StaticID, locked layer, unsupported Boolean input, malformed JSON result, and a forced failure after setup; assert no unrelated document is modified and outcome is not misreported as success.
- [x] **Step 3: Run `npm run test:integration` on the supported Windows/CorelDRAW environment** and require actual `verification.json` evidence before marking recipes supported.
- [x] **Step 4: If CorelDRAW is unavailable, keep scenarios present but report them as not executed; do not convert static or synthetic tests into live claims.

#### Task A4: Extend preview to `page | selection`

**Files:**
- Modify: `src/tools.ts` near `corel_preview`
- Modify: `powershell/CorelAutomation.psm1` preview/export implementation
- Modify: `src/executor.ts` preview artifact validation if result shape changes
- Modify: `src/server.ts` preview attachment handling if needed
- Modify: `skills/coreldraw-automation/references/tools.md`
- Modify: `skills/coreldraw-automation/SKILL.md`
- Test: `test/preview-selection.test.mjs`
- Modify: `test/integration.mjs`

**Interfaces:**
- Consumes: existing preview schema, export staging path, `document_id`, `corel_get_job(include_preview=true)`.
- Produces: `corel_preview.target: "page" | "selection"`, existing `max_size` limit, explicit empty-selection error, and cleanup restoration of page/selection/document state.

- [x] **Step 1: Add failing schema/unit tests** for default `target="page"`, accepted `selection`, rejection of unknown target, and empty-selection error mapping.
- [x] **Step 2: Run focused tests** and confirm the new target is not yet accepted.
- [x] **Step 3: Implement the schema and PowerShell path** by reusing the typed export/staging route; selection mode must target the exact current document, reject zero selected shapes, bound the longest image side by `max_size`, and preserve original selection/page in independent cleanup steps.
- [x] **Step 4: Add executor/server assertions** so returned preview artifacts remain inside the job directory, are regular files, are size-bounded, and are attached only for succeeded preview jobs.
- [x] **Step 5: Add live integration tests** for page preview, non-empty selection preview, empty selection, changed active page, and failure during cleanup; then run `npm test` and `npm run test:integration`.

### Package B: Typed Additional Export Formats

#### Task B1: Add JPEG and TIFF contracts first

**Files:**
- Modify: `src/tools.ts` `corel_export` format schema and format-specific options
- Modify: `powershell/CorelAutomation.psm1`
- Modify: `skills/coreldraw-automation/references/tools.md`
- Modify: `skills/coreldraw-automation/references/api-v27.md` only with verified constants/semantics
- Test: `test/export-format-schema.test.mjs`
- Modify: `test/integration.mjs`

**Interfaces:**
- Consumes: current staging/commit/overwrite/request_id export pipeline.
- Produces: explicit `jpg/jpeg` and `tif/tiff` aliases with only supported typed options, bounded dimensions, and unchanged destination-on-failure behavior.

- [x] **Step 1: Write schema tests** for aliases, required raster sizing, max pixel budget, overwrite behavior, and rejection of unsupported filter options.
- [x] **Step 2: Implement typed JPEG/TIFF filter mapping** using verified API 27 constants; do not expose an arbitrary options dictionary.
- [x] **Step 3: Add failure-safe staging tests** proving a failed export does not publish a partial file or replace an existing destination.
- [x] **Step 4: Run live tests on temporary documents** and inspect output existence, nonzero size, format signature where practical, page/selection range, and restoration of document state.

#### Task B2: Add DXF and PLT only after live filter verification

**Files:**
- Modify: `src/tools.ts`
- Modify: `powershell/CorelAutomation.psm1`
- Modify: `skills/coreldraw-automation/references/tools.md`
- Modify: `skills/coreldraw-automation/references/api-v27.md`
- Test: `test/export-vector-format.test.mjs`
- Modify: `test/integration.mjs`

**Interfaces:**
- Consumes: B1 export contract and existing geometry-invariant checks.
- Produces: typed `dxf` and `plt` options only where CorelDRAW 27 behavior is observed and documented.

- [x] **Step 1: Add tests that initially mark unverified filters unsupported**, preventing schema advertisement before verification.
- [x] **Step 2: Verify filter direction, optional parameters, dialogs, scale, units, and `Finish` behavior against CorelDRAW 27 in a temporary document.
- [x] **Step 3: Implement each format separately with explicit geometry/scale checks; reject unsupported options rather than passing them through.
- [x] **Step 4: Run integration tests for page/all/selection and failure preservation; update docs only after evidence is recorded.

#### Task B3: Defer AI/EPS and define go/no-go evidence

**Files:**
- Modify: `docs/donor-project-comparison.md`
- Modify: `docs/verification.md`
- Modify: `skills/coreldraw-automation/references/tools.md`

- [x] **Step 1: Record AI/EPS as unsupported/deferred unless a real production scenario, verified filter direction, text/curve policy, and output validation are available.
- [x] **Step 2: Ensure documentation never claims support merely because an enum constant exists.

### Package C: Consistency, Diagnostics, and Optional User Controls

#### Task C1: Add schema/skill/example consistency checker

**Files:**
- Create: `scripts/check-contracts.mjs`
- Modify: `package.json`
- Modify: `skills/coreldraw-automation/references/tools.md`
- Modify: `skills/coreldraw-automation/SKILL.md`
- Test: `test/contracts-check.test.mjs`
- Modify: `.github/workflows/` or existing CI configuration if present

**Interfaces:**
- Consumes: `src/tools.ts` tool registry/schemas and repository skill/reference files.
- Produces: a deterministic checker invoked by `npm test` or a dedicated `npm run check:contracts`.

- [x] **Step 1: Define checker tests** for missing tool names, missing required schema fields, stale enum values, and undocumented defaults.
- [x] **Step 2: Implement static checks using the project's existing TypeScript build output or a deliberately bounded metadata export; do not execute tool handlers or COM.
- [x] **Step 3: Add checks for the six recipe filenames, preview target values, export aliases, and public descriptions matching actual schemas.
- [x] **Step 4: Run the checker against the repository and add it to the standard verification command.

#### Task C2: Add optional disabled-tool configuration in user storage

**Files:**
- Modify: `src/types.ts`
- Modify: `src/tools.ts`
- Modify: `src/server.ts`
- Modify: `src/index.ts`
- Create: `src/config.ts`
- Test: `test/config-tools.test.mjs`
- Modify: `README.md`
- Modify: `docs/architecture.md`

**Interfaces:**
- Consumes: current tool definitions and MCP registration.
- Produces: an opt-in local allow/deny list stored under `%LOCALAPPDATA%\\CorelDrawMcp`, applied before tool submission, with atomic writes and fail-closed handling for malformed configuration.

- [x] **Step 1: Write tests** for default enabled tools, disabled tool hidden/rejected, malformed config disabling rather than enabling dangerous tools, and read-only diagnostics remaining available.
- [x] **Step 2: Implement config loading with a user-writable path**, never writing beside installed code; preserve current server behavior when no config exists.
- [x] **Step 3: Apply the policy before `JobManager.submit` and expose effective/registered counts in `corel_status` without revealing secrets.
- [x] **Step 4: Document this as a convenience/surface-reduction control, not authentication or a sandbox.

#### Task C3: Add a bounded portable diagnostic report

**Files:**
- Modify: `src/artifacts.ts`
- Modify: `src/local-operations.ts`
- Modify: `src/tools.ts`
- Modify: `src/server.ts`
- Test: `test/diagnostics.test.mjs`
- Modify: `README.md`

**Interfaces:**
- Consumes: existing durable `Job` report, cleanup/error codes, and artifact inventory.
- Produces: a local operation that writes an atomic allowlist report containing versions, stage, status, timing, error codes, cleanup flags, and a technical diagnostic ID; excludes request IDs, paths, names, VBA, raw errors, and arbitrary result payloads by default.

- [x] **Step 1: Write redaction tests** with synthetic paths, Unicode, VBA code, raw error messages, request IDs, and result payloads.
- [x] **Step 2: Implement an atomic external report writer** with explicit overwrite behavior and a unique/default destination; no COM call.
- [x] **Step 3: Add tests for failed writes, repeated reports, unknown outcomes, pruned details, and cleanup-incomplete jobs.
- [x] **Step 4: Document that expanded details require explicit user action and that the report is not a guarantee of complete anonymization.

#### Task C4: Add opt-in artifact quota/cleanup only with reservation preservation

**Files:**
- Modify: `src/artifacts.ts`
- Modify: `src/local-operations.ts`
- Modify: `src/tools.ts`
- Test: `test/artifacts-cleanup.test.mjs`
- Modify: `README.md`

- [x] **Step 1: Add tests proving running, timed_out, unknown, cleanup-incomplete, and retained-GMS jobs are protected.
- [x] **Step 2: Implement list/prune behavior using existing `prune_token`, inventory digest, `.prune-lock`, and compact durable reports; never remove the reservation directory or request fingerprint.
- [x] **Step 3: Test restart behavior: after details are pruned, reusing the same `request_id` still returns the prior outcome or conflict and never executes the mutation again.
- [x] **Step 4: Keep cleanup explicitly invoked; no background TTL deletion.

### Final Cross-Package Validation

**Files:**
- Modify: `docs/verification.md`
- Modify: `docs/donor-project-comparison.md`

- [x] Run `npm test` after all package tasks.
- [x] Run TypeScript build and PowerShell syntax validation.
- [x] Run `npm run check:contracts`.
- [x] Run `npm run test:integration` on Windows with CorelDRAW 27 and inspect the produced `verification.json`; do not claim live support without it.
- [x] Verify existing idempotency, mutex, timeout, owner-loss, artifact-prune, cleanup, and API-metadata tests remain green.
- [x] Verify no new tool was added for the six recipes; only preview/schema/export/config/diagnostic public contracts changed as specified.
- [x] Update documentation with only observed CorelDRAW behavior and record deferred AI/EPS, Woodman, and in-process HTTP items as out of scope.

## Final Status

Implementation and validation are complete for the repository-scoped behavior
covered by this plan. The latest sequential checks passed: `npm test` 88/88,
`npm run check:contracts`, the main live integration report with 19 checks,
the recipe report with 13 checks and cleanup completion, and the previously
recorded extended report with eight groups and 18 executable examples.

Explicit limitations remain: AI/EPS production support is deferred; DXF/PLT
evidence is a CorelDRAW temporary-document round-trip rather than production
material validation; preview cleanup fault injection in a user CorelDRAW
process is not claimed; arbitrary VBA remains intentionally unsandboxed; and
C2 exposes a denylist-only `disabled_tools` contract, not a separate allowlist.
