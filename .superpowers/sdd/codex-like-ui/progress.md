# SDD ledger — plan: docs/superpowers/plans/2026-11-09-codex-like-agent-ui.md

## Setup

- Execution method: Agent Manager isolated worktrees; implementer then independent reviewer per task.
- Parent branch: `git`; product changes stay isolated until review/integration.
- Parent baseline: `28b6cb4` at session start; uncommitted plan remains in parent only.
- Task 1 brief is supplied directly to the Agent Manager implementer because the plan is uncommitted.

## Preflight conflict scan

| Scope | Shared file/interface | Finding | Ruling |
|---|---|---|---|
| Task 1 ↔ Task 2 | `webui/tests/layout.spec.ts`, shell selectors | Task 1 defines selectors/tests; Task 2 implements them. | Intentional producer/consumer dependency; Task 1 must not implement shell. |
| Task 1 ↔ Task 3 | `webui/tests/layout.spec.ts`, composer/main surface | Task 1 tests composer presence; Task 3 refines composer. | Keep assertions semantic and stable; visual polish remains Task 3. |
| Task 1 ↔ Task 4 | secondary surface visibility | Task 1 empty-state assertion precedes Task 4 disclosure implementation. | Test only absence/collapsed state, not implementation mechanism. |
| Task 2 ↔ Task 3 | `App.tsx`, `styles.css`, chat surface | Shell and chat styling share layout files. | Task 2 owns shell structure; Task 3 owns composer/message presentation; later task may modify shared CSS without rewriting state logic. |
| Task 2 ↔ Task 4 | `App.tsx`, secondary panel placement | Task 2 creates surface slots; Task 4 fills them. | Preserve component ownership and callbacks; no duplicate mutation paths. |
| Task 3 ↔ Task 4 | inline preview/activity result area | Task 3 defines result presentation; Task 4 defines technical disclosure. | Keep preview inline and operations collapsible; no API changes. |
| Task 4 ↔ Task 5 | recovery/settings/accessibility styling | Task 4 preserves semantics; Task 5 polishes tokens/focus. | Safety identifiers and accessible names remain stable. |
| Task 5 ↔ Task 6 | tests/build | Task 5 adds focused a11y checks; Task 6 runs full gate. | Full verification is final evidence, not a substitute for focused tests. |
| Task 1 self-consistency | spec, layout test | Spec and test target same chat-first shell and 390px behavior. | Proceed. |
| Task 2 self-consistency | shell files/tests | Existing callbacks/state are explicitly preserved. | Proceed. |
| Task 3 self-consistency | chat/message/preview files | Existing props and labels are explicit. | Proceed. |
| Task 4 self-consistency | panels and regression tests | Existing labels/data-testid are explicit. | Proceed. |
| Task 5 self-consistency | CSS/access tests | Accessibility and reduced-motion requirements are testable. | Proceed. |
| Task 6 self-consistency | verification commands | Commands are repository-supported and do not require live CorelDRAW. | Proceed. |

## Rulings

- Ruling: Use the current Russian labels and existing test selectors as compatibility contracts — they are already exercised by the panel suite — cost if wrong: selector churn and hidden regression.
- Ruling: Do not commit from Agent Manager workers because the user authorized isolated execution, not history mutation — cost if wrong: integration requires manual cherry-pick/merge cleanup.
- Ruling: Keep the uncommitted plan in the parent and repeat its relevant task requirements in each worker prompt — cost if wrong: a worker may implement against stale or missing plan text.

Task 1: complete (uncommitted isolated worktree, review approved after fix round; spec + layout tests only).
Task 2: complete (shell + fix round, review approved; focused build and UI suite reported pass by implementer).
