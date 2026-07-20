## CLI UX — file paths are shortened tails; boundary markers for dead-end traces

**Status:** todo
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (D8 / `--files` path issue and D4
boundary-marker item from the UX section)

### D8 — `--files` paths are shortened tails, not solution-root-relative

`rig tree --files` (and file:line references in `reaches`/`path` output) renders paths like
`src/Audits/…` — a shortened tail, not the solution-root-relative path (`src/audits/src/Audits/…`). Agents
and callers cannot open these paths directly; every session had to glob by basename.

Fix: emit paths relative to the solution root (the `.rig` store's anchor directory) or as absolute paths.
Ties to the `--source` / quote-source mode (D8 in the register).

Evidence: noted across every agent session that used `--files` output.

### D4 — Boundary markers in `tree`/`reaches` for dead-end traces

When a trace dead-ends at a known boundary — `Process.tell`, `[ClientAction]`, `Activator.CreateInstance`,
an interface with no in-scope impl — rig silently stops. The user sees a truncated tree with no indication
that effects BEYOND this point are invisible.

Fix: emit a `⊘ boundary: echo .tell (effects beyond invisible)` leaf instead of silently stopping. Would
cover the cases that map to the "rig cannot adjudicate" bucket from the audit.

Evidence: `RtfPipe.Rtf.ToHtml`; Medicare dialog proxy; webhook Redis handoff (F6 in the register).

Also cover custom `DelegatingHandler.SendAsync` pipelines: calls made inside a handler are currently invisible
from the client wiring path, so the tree should disclose the handler/framework seam instead of silently ending.
This absorbs the former VS-G13 residual from the completed deferred-delegate card.

### Relationship

These two items are independent UX fixes that both improve the actionability of rig output. Grouping them
here to avoid one-item files; split into separate tasks if effort diverges.
