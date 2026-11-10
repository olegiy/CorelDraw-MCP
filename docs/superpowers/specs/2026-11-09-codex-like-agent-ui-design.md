# Codex-like CorelDRAW Agent UI Design

## Intent

CorelDRAW Agent should read as a calm, chat-first workspace rather than a stack of equally weighted control cards. The primary task is the conversation with the agent: the current document and run state provide context, messages provide the work history, and the composer provides the next action. The visual direction borrows the restrained hierarchy of Codex Desktop without copying its branding or introducing a new UI dependency.

## Shell and visual hierarchy

The application shell has two regions on wide screens:

- A compact left sidebar (`.app-sidebar`) contains the product identity, `Новая переписка`, the recent conversation list, and the existing `Настройки модели` affordance. It is navigation, not a second workspace.
- The main region (`.app-main`) contains a context bar (`.context-bar`), the conversation surface (`.chat-surface`), and secondary surfaces that are explicitly collapsed or conditional (`.secondary-surface`). The main content is constrained to a readable measure and may grow vertically.

The shell root is `.app-shell`. The conversation list is `.conversation-list`; the visible chat area is `.chat-surface`. These classes are stable structural selectors, not styling hooks for business logic. Existing API calls, state ownership, callbacks, request IDs, streaming, and safety semantics remain unchanged.

The context bar is the persistent orientation point. It shows `CorelDRAW Agent`, the selected document name or `Документ не выбран`, the current run status, and `Стоп` only while a request is pending or a run is active. Status retains its existing `role="status"` semantics. A safety/recovery barrier is conditional but never hidden behind an ordinary disclosure when it blocks execution.

## States

### Empty

With no selected run, the main area presents a short invitation and the composer. The visual flow must not contain empty technical cards for `История`, `Файлы`, or `Операции`; navigation and actionable context may remain available in the sidebar or context bar. Preview and operation details render only when they have content.

### Active

While a run is pending or active, the context bar keeps the status and `Стоп` action easy to find. The message stream remains visible, the composer follows existing disabled behavior, and progress/warning semantics are retained. Activity details may be summarized in a compact secondary surface rather than occupying the first visual level.

### Stopped or completed

The final status remains in the context bar and the conversation retains messages, artifacts, warnings, and saved preview results. A stopped run must not be presented as cancelled or silently removed. The next conversation is an explicit navigation action and does not replay an old mutation.

### Safety barrier

When readiness is degraded or the outcome is uncertain, the recovery surface is an alert near the context/status area and remains visible in the primary flow. Existing barrier text, `global-barrier` test id, reconciliation action, and restart guidance remain semantically unchanged. A new conversation does not clear or weaken the barrier.

## Responsive behavior

At a 390px viewport, the sidebar becomes a compact top or vertically ordered region without requiring horizontal scrolling. The main region and all descendants use `min-width: 0`; long document paths, model URLs, operation summaries, and artifact names wrap or use safe overflow handling. The composer controls wrap within the viewport, and no page-level horizontal overflow is permitted (`document.documentElement.scrollWidth <= innerWidth`). All controls remain keyboard reachable and retain their accessible names.

## Accessibility

- The page has one `h1`, with subsequent headings preserving a logical hierarchy.
- Existing labels and roles are preserved exactly, including `Целевой документ`, `Сообщение агенту`, `Агент создаёт новый документ`, `Отправить`, `Стоп`, and `Настройки модели`.
- Every icon-only control has an accessible name and a visible tooltip/title; text controls remain usable without relying on icons or color.
- Sidebar navigation, context actions, disclosures, native settings dialog, and composer are keyboard operable.
- `:focus-visible` remains visible against the neutral surfaces; disabled controls remain distinguishable without relying on color alone.
- Reduced-motion preferences disable nonessential transitions.
- Existing Markdown sanitization, artifact-link behavior, dialog semantics, and safety alerts are not weakened.

## Design tokens

The visual system uses CSS custom properties with a quiet neutral palette and one blue accent:

| Token | Purpose | Direction |
| --- | --- | --- |
| `--color-canvas` | application background | warm/neutral near-white |
| `--color-surface` | sidebar, composer, and conditional surfaces | white or near-white |
| `--color-text` | primary readable text | dark neutral |
| `--color-muted` | supporting/status copy | subdued neutral |
| `--color-border` | separators and field borders | low-contrast neutral |
| `--color-accent` | primary action and focus relationship | restrained blue |
| `--color-danger` | stop and safety states | high-contrast red |
| `--space-1` through `--space-6` | consistent spacing scale | compact, predictable increments |
| `--radius-sm` and `--radius-md` | restrained control/surface rounding | avoid excessive pill cards |

Typography stays on the existing system font stack. No external font, image asset, icon library, layout library, or new runtime dependency is required.

## Non-goals

- Do not change backend routes, API schemas, MCP dispatch, agent-loop limits, stream recovery, idempotency, unknown-outcome handling, or safety-barrier rules.
- Do not reintroduce removed permission controls, Vision flags, PNG-preview consent controls, or hidden provider parameters.
- Do not redesign document/file operation payloads, CorelDRAW automation behavior, or Credential Manager handling.
- Do not add a dashboard, analytics view, persistent technical log, or a second independent navigation model.
- Do not make live CorelDRAW smoke tests mandatory for the visual redesign.
- Do not implement later shell, chat, secondary-panel, or polish tasks in this baseline task; this document and its failing layout contract are the deliverable for Task 1.
