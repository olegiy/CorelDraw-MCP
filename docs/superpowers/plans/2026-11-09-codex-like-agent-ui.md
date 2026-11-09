# Лаконичный интерфейс CorelDRAW Agent Implementation Plan

> **Для агентных исполнителей:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Цель:** Перестроить web-панель CorelDRAW Agent в спокойный chat-first интерфейс, визуально близкий к Codex Desktop: чат и текущая задача находятся в центре, история — в компактном сайдбаре, а редко используемые операции не перегружают основной экран.

**Архитектура:** Сохранить существующий React/Vite application state и API orchestration в `App.tsx`, но заменить плоскую раскладку на shell из сайдбара, верхней строки контекста, основной области переписки и компактных раскрываемых служебных секций. Существующие компоненты действий документа, файлов, recovery, операций и preview остаются владельцами поведения; их представление переносится в disclosure/menu/drawer-контейнеры без изменения HTTP-контрактов, Run lifecycle, idempotency или safety barrier.

**Технологический стек:** React 19, TypeScript 7, Vite 8, существующие компоненты `webui/src/components`, CSS без новой UI-библиотеки, Playwright 1.64 для UI/fullstack проверок.

**Спецификация:** Требование из обсуждения: интерфейс должен быть лаконичным и ненагруженным, по композиции напоминать Codex Desktop. На первом экране должны доминировать список переписок, текущий документ, сообщения и поле ввода. Действия CorelDRAW, файловые настройки, технические операции, профиль модели и recovery показываются контекстно или раскрываются явно.

## Глобальные ограничения

- Не изменять backend/API, MCP dispatch, agent loop, streaming, request idempotency или семантику неизвестного результата.
- Не возвращать удалённые permission-контролы, Vision-флаги, PNG consent controls или скрытые API-параметры.
- Не ослаблять safety barrier: неопределённость, незавершённый cleanup, остановка и readiness остаются видимыми и блокирующими там, где это уже предусмотрено.
- Не менять тексты и доступность существующих `aria-label`, `role`, `data-testid` без необходимости; сохранённые Playwright-сценарии должны продолжать находить функциональные элементы.
- Не добавлять иконки-заглушки без доступного имени; каждая icon-only кнопка получает `aria-label` и tooltip/title.
- Не добавлять новую зависимость для layout, иконок или компонентов.
- Сохранять мобильный режим, клавиатурную навигацию, `:focus-visible`, native `dialog` и Markdown/XSS-ограничения.
- Проверки выполняются штатным gate `scripts/Verify-CorelAgent.ps1`; live CorelDRAW smoke не включать автоматически.

## Файловая карта

- Modify: `webui/src/App.tsx` — новый semantic shell, состояние выбранной панели/меню и размещение существующих блоков; бизнес-операции не переписывать.
- Modify: `webui/src/styles.css` — Codex-like visual system, sidebar/main layout, compact controls, responsive breakpoints, reduced-motion behavior.
- Modify: `webui/src/components/Chat.tsx` — лаконичный composer с autosize/submit affordance, сохраняя label, Enter/Shift+Enter и disabled semantics.
- Modify: `webui/src/components/MessageList.tsx` — визуальная иерархия user/agent сообщений, inline artifact/result treatment без изменения Markdown policy.
- Modify: `webui/src/components/DocumentPanel.tsx` — compact document context/actions, сохраняя labels и callbacks.
- Modify: `webui/src/components/FileActions.tsx` — раскрываемый file actions surface, сохраняя все существующие поля и tool arguments.
- Modify: `webui/src/components/RunPanel.tsx` — сворачиваемый technical activity view с коротким summary.
- Modify: `webui/src/components/PreviewPanel.tsx` — inline result card, отображаемый только при наличии preview.
- Modify: `webui/src/components/RecoveryPanel.tsx` — alert surface, визуально заметный только при barrier/readiness problem.
- Modify: `webui/src/components/SettingsDialog.tsx` — compact settings trigger, native modal contract и Credential Manager flow без изменений.
- Modify: `webui/src/components/*.tsx` only as needed for presentational wrappers; не переносить stateful API logic в CSS-only hacks.
- Modify: `webui/tests/*.spec.ts` — добавить визуально-структурные и accessibility assertions, обновлять селекторы только при изменении DOM ownership.
- Create: `webui/tests/layout.spec.ts` — отдельные проверки shell, collapsed secondary surfaces и mobile overflow.
- Create: `docs/superpowers/specs/2026-11-09-codex-like-agent-ui-design.md` — утверждённая краткая design spec, если исполнительный процесс требует отдельного design artifact перед кодом.

## Review Focus

- Empty state без Run: пользователь видит понятный prompt и composer, но не видит пустые технические карточки; закрепить в `layout.spec.ts`.
- Active/stopping Run: статус и `Стоп` остаются доступны в верхней области, а сообщения и safety state не исчезают; закрепить в существующих progress/pendingStop/safety тестах.
- Safety barrier/recovery: barrier не прячется в обычное меню и сохраняет `global-barrier`, `CLEANUP_NOT_PROVEN`, reconciliation и Stop/Start guidance; закрепить в recovery/reviewBarrier.
- Narrow viewport: sidebar и composer не создают горизонтальный overflow, функциональные поля остаются доступны; закрепить в `layout.spec.ts` и сохранить launcher acceptance на 390 px.
- Existing interaction contracts: document/file/settings selectors, artifact links, preview binding и history selection продолжают работать; прогнать существующие Playwright suites.

---

## Этап 1: Зафиксировать UI-контракт и визуальный baseline

**Файлы:**

- Create: `docs/superpowers/specs/2026-11-09-codex-like-agent-ui-design.md`
- Create: `webui/tests/layout.spec.ts`
- Modify: `webui/tests/access.spec.ts` only if a stable shell assertion is needed.

**Интерфейсы:**

- Produces: стабильные shell selectors/classes: `.app-shell`, `.app-sidebar`, `.app-main`, `.context-bar`, `.conversation-list`, `.chat-surface`, `.secondary-surface`.
- Produces: тестовые ожидания, не зависящие от конкретных CSS pixel values для функциональных сценариев.

- [ ] **Шаг 1: Описать design spec** — зафиксировать shell, приоритеты экрана, states (empty, active, stopped, barrier), mobile behavior, typography/spacing/color tokens и non-goals.
- [ ] **Шаг 2: Написать failing layout tests** — проверить heading, sidebar, new conversation, conversation list, context bar, composer, отсутствие видимых пустых `История/Файлы/Операции` карточек в empty state и отсутствие horizontal overflow при viewport 390 px.
- [ ] **Шаг 3: Запустить только новые тесты** — `npm --prefix webui test -- layout.spec.ts`; ожидаемо получить FAIL по отсутствующим shell selectors/композиции.
- [ ] **Шаг 4: Проверить дизайн spec вручную против текущего `App.tsx` и списка Playwright selectors** — убедиться, что ни один обязательный функциональный label не потерян.

## Этап 2: Перестроить application shell и sidebar

**Файлы:**

- Modify: `webui/src/App.tsx`
- Modify: `webui/src/styles.css`
- Modify: `webui/tests/layout.spec.ts`
- Modify: `webui/tests/runsPagination.spec.ts` only when history ownership moves.

**Интерфейсы:**

- Consumes: existing `runs`, `run`, `conversation`, `setConversation`, message/operation/preview state and current action handlers.
- Produces: `.app-shell` with `.app-sidebar` and `.app-main`; sidebar exposes `Новая переписка`, recent run buttons and settings trigger; main exposes `.context-bar`, `.chat-surface` and composer area.
- Produces: run selection behavior identical to current history buttons, including document/output directory restoration.

- [ ] **Шаг 1: Implement shell markup** — move `SettingsDialog` to sidebar footer, render recent runs as compact navigation buttons, move `Новая переписка` to sidebar header, and keep unique accessible names.
- [ ] **Шаг 2: Implement context bar** — show app title, selected document name or `Документ не выбран`, current run status and `Стоп` only while pending/active; keep status `role` and stop behavior unchanged.
- [ ] **Шаг 3: Add CSS layout** — desktop two-column shell with fixed-width compact sidebar, centered constrained chat column, neutral background, subtle borders and no card around every message.
- [ ] **Шаг 4: Add responsive CSS** — at 720 px and below turn sidebar into a compact top/scrollable region or collapsible section; enforce `min-width: 0`, wrapping and no horizontal overflow.
- [ ] **Шаг 5: Run focused tests** — `npm --prefix webui test -- layout.spec.ts runsPagination.spec.ts access.spec.ts`; expected PASS for shell and history behavior.

## Этап 3: Сделать чат главным экраном

**Файлы:**

- Modify: `webui/src/components/Chat.tsx`
- Modify: `webui/src/components/MessageList.tsx`
- Modify: `webui/src/components/PreviewPanel.tsx`
- Modify: `webui/src/styles.css`
- Modify: `webui/tests/chat.spec.ts`, `webui/tests/messages.spec.ts`, `webui/tests/historyPreview.spec.ts` only where DOM placement changes.

**Интерфейсы:**

- Consumes: unchanged `messages`, `Preview`, `busy`, `submit` props and artifact URLs.
- Produces: composer retaining `getByLabel('Сообщение агенту')`, `getByRole('button', {name: 'Отправить'})`, Enter submit and Shift+Enter newline.
- Produces: message list with semantic articles and visually distinct role treatment; preview/artifact links remain in the conversation result area.

- [ ] **Шаг 1: Write/adjust interaction assertions** — empty composer disabled while busy, Enter submits, Shift+Enter does not submit, Markdown image remains blocked, artifact download remains accessible.
- [ ] **Шаг 2: Refactor `Chat` markup** — use a compact bordered composer surface, visually secondary hint text and a submit control with accessible name; do not change message state or max length.
- [ ] **Шаг 3: Refactor `MessageList` presentation** — add role-specific classes/data attributes, readable max-width and spacing; keep raw role text and Markdown renderer policy compatible with current tests.
- [ ] **Шаг 4: Render preview inline** — keep `PreviewPanel` null when no preview and preserve `preview-binding`, image alt and saved-preview disclaimer.
- [ ] **Шаг 5: Run focused tests** — `npm --prefix webui test -- chat.spec.ts messages.spec.ts historyPreview.spec.ts progress.spec.ts`; expected PASS.

## Этап 4: Убрать второстепенные панели с первого экрана

**Файлы:**

- Modify: `webui/src/components/DocumentPanel.tsx`
- Modify: `webui/src/components/FileActions.tsx`
- Modify: `webui/src/components/RunPanel.tsx`
- Modify: `webui/src/components/RecoveryPanel.tsx`
- Modify: `webui/src/components/SettingsDialog.tsx`
- Modify: `webui/src/App.tsx`
- Modify: `webui/src/styles.css`
- Modify: `webui/tests/files.spec.ts`, `webui/tests/readonly.spec.ts`, `webui/tests/recovery.spec.ts`, `webui/tests/reviewBarrier.spec.ts`, `webui/tests/settings.spec.ts` as needed.

**Интерфейсы:**

- Consumes: existing callbacks `action`, `refresh`, `setDocumentId`, `setOutputDirectory`, `save`, and current API behavior.
- Produces: document context trigger/menu with `Целевой документ` select and existing document actions; file actions in an explicit secondary surface; technical operations behind `details`; recovery as an always-visible conditional alert.
- Produces: no duplicate controls that can send the same mutation through two different state paths.

- [ ] **Шаг 1: Add failing visibility assertions** — in empty state secondary surfaces are collapsed/absent from the visual flow; after opening the document surface all existing labels/actions are visible and enabled according to current state.
- [ ] **Шаг 2: Refactor `DocumentPanel`** — show selected document summary by default; put refresh/select/inspect/save/close/preview actions under an explicit document actions surface while retaining exact labels used by tests.
- [ ] **Шаг 3: Refactor `FileActions`** — expose it through a compact `details` or menu-like panel; preserve path, format, range, overwrite copy and tool arguments exactly.
- [ ] **Шаг 4: Refactor `RunPanel`** — render a single concise activity summary by default and keep operation details/JSON/report links inside closed `details` items.
- [ ] **Шаг 5: Refine `RecoveryPanel`** — use alert styling and placement near context/status, render nothing on healthy state, preserve all safety text and `global-barrier` test id.
- [ ] **Шаг 6: Keep settings modal contract** — replace only trigger styling/placement; preserve native modal, labels, key clearing and Credential Manager save behavior.
- [ ] **Шаг 7: Run focused tests** — `npm --prefix webui test -- files.spec.ts readonly.spec.ts recovery.spec.ts reviewBarrier.spec.ts settings.spec.ts safetyState.spec.ts`; expected PASS.

## Этап 5: Typography, interaction polish and accessibility

**Файлы:**

- Modify: `webui/src/styles.css`
- Modify: affected components only for missing semantics.
- Modify: `webui/tests/layout.spec.ts`, `webui/tests/access.spec.ts`.

**Интерфейсы:**

- Produces: tokenized CSS variables for background, surface, text, muted text, border, accent, danger and spacing; no dependency on external fonts/assets.
- Produces: visible focus ring, keyboard-operable disclosures/buttons, readable contrast, reduced-motion safe transitions.

- [ ] **Шаг 1: Add accessibility assertions** — page has one h1, heading hierarchy remains valid, icon-only controls have accessible names, tab navigation reaches sidebar/context/composer controls, no visible element is clipped at 390 px.
- [ ] **Шаг 2: Implement visual tokens** — use restrained neutral palette and one blue accent; remove excessive rounded cards, saturated status blocks and duplicate borders.
- [ ] **Шаг 3: Implement focus/hover/disabled states** — keep `:focus-visible`, ensure disabled controls remain understandable, preserve danger contrast for stop and safety alerts.
- [ ] **Шаг 4: Implement reduced-motion rule** — disable nonessential transitions under `prefers-reduced-motion: reduce`.
- [ ] **Шаг 5: Run focused tests** — `npm --prefix webui test -- layout.spec.ts access.spec.ts`; expected PASS.

## Этап 6: Full verification and regression review

**Файлы:**

- Modify: tests only for proven selector/structure changes.
- No backend files expected.

- [ ] **Шаг 1: Typecheck and build UI** — `npm --prefix webui run build`; expected exit code 0.
- [ ] **Шаг 2: Run complete UI suite** — `npm --prefix webui test`; expected all Playwright tests pass, with no live CorelDRAW requirement.
- [ ] **Шаг 3: Run repository verification gate** — `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-CorelAgent.ps1`; expected exit code 0 and no regressions in MCP/Python/contracts/OpenAPI/UI checks.
- [ ] **Шаг 4: Inspect diff and responsive behavior** — `git diff --check`, review changed files, verify desktop and 390 px screenshots manually if available; confirm no accidental API or safety changes.
- [ ] **Шаг 5: Record verification evidence** — note exact commands/results in the implementation handoff; do not claim live CorelDRAW coverage unless opt-in fixture run was separately executed.

## Definition of Done

- Empty state reads as a calm chat workspace rather than a collection of panels.
- Sidebar shows only app identity, new conversation, recent conversations and settings.
- Main area shows context, messages, inline results and composer; active stop/status remains easy to find.
- Document/file operations and technical operation details are available through explicit compact surfaces, not permanently stacked cards.
- Recovery/safety barriers remain prominent and semantically unchanged.
- Existing document, file, preview, history, settings, stream recovery and safety tests pass.
- UI build and full repository verification gate pass with fresh command output.
