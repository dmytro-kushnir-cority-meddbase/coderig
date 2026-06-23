# rig UX research — panel findings (2026-06-23)

A panel of 6 independent agents (3 Opus, 3 Sonnet) each used `rig` to do a **real developer task** on
a real indexed store, instructed to keep a friction log and rank the improvements they'd most want. Two
repos for scale diversity: **MedDBase** (legacy C# monorepo, ~340k symbols, 9 commit stores) and
**coderig** (rig itself, small modern CLI). Each agent had the corrected SKILL.md/REFERENCE.md + live
`--help` as its guide, so findings reflect the *tool*, not stale docs.

| # | Model | Repo | Task | Surface exercised |
|---|---|---|---|---|
| A | Opus | MedDBase | Audit-logging inventory of a feature's DB/external writes | `reaches`/`tree --view effects`/`--only` |
| B | Opus | MedDBase | MR blast-radius review across two commits | `impact --base/--head/--structural` |
| C | Opus | MedDBase | Concurrency/perf hazard hardening | `derive` hazards / `tree --view hazards`/`path` |
| D | Sonnet | coderig | Safe-to-change a core method (signature blast radius) | `callers`/`refs`/`path`/`symbols` |
| E | Sonnet | coderig | 30-minute onboarding / architecture sketch | `entrypoints`/`derive`/`tree --view summary` |
| F | Sonnet | both | Feed call-tree to an LLM for redundancy + side-effect analysis | `--format llm`/`llm-ids`/`--suppress` |

**Headline:** rig's individual answers are trusted and its drill-downs (`rig path`, the fan-out disclosure,
the per-effect evidence + loop annotations) drew unprompted praise from nearly every agent. The damage is
concentrated in **(1) silent failures that look like authoritative answers**, **(2) vocabulary that doesn't
compose across commands**, and **(3) signal buried under noise with no triage lever**. Most fixes are cheap
(labels, headers, docs, a filter flag); a few (hazard triage, unmodeled-call surfacing) are design work.

---

## Ranked improvement backlog

Tags: **[tool]** code change · **[doc]** docs/help only · **[cheap]** small · **[design]** needs thought.
Severity reflects how badly it misleads or blocks, × how many agents independently hit it.

### P1 — Trust / correctness (a confident answer that is silently wrong or misleading)

1. **Empty/zero results must signal *incompleteness*, not safety.** `[tool]` `[design]` — *Sev: critical (A, also B, F).*
   Agent A ran `reaches --only http,soap,queue` on a DB-write path, got **0**, and nearly reported "no
   external side effects" — but a real `TriggerDocumentWebhook` outbound call fires there; it's invisible
   because no rule models it. Two silent-failure modes compound: **unknown `--only` tokens match nothing
   with no warning** (A guessed `webhook`/`llm`/`io:write` — all silently empty), and **unmodeled calls on
   the path leave no trace**. Fixes: warn on unrecognized effect tokens; on an empty effect query, note
   "N first-party calls on these paths matched no effect rule"; consider `--show-unmodeled-calls` flagging
   `*Webhook*`/`*Notify*`/external-assembly call sites. An empty result currently reads as "clean" and can be
   actively wrong on the exact question asked.

2. **Hazard triage is not yet trustworthy: dedup-by-method, fix severity, exempt CLR-safe constructs.** `[tool]` `[design]` — *Sev: high (C).*
   The two largest store-wide hazard clusters are **false positives presented as `high`** (26× on one
   settings-save method, 16× on a `#cctor`), while the genuine bugs (a real `DocumentEntity` dual-write, an
   `AccountSecurity` static-set race) are `low` — so **sorting by severity surfaces the worst noise first.**
   One method → N hazards inflates counts ("112 race_windows" is meaningless). Concrete detector FPs to kill:
   (a) **conditional overwrite** (`if (x != Settings.Y) Settings.Y = x`) misread as RMW — the write value is
   independent of the read, no lost-update class; (b) **`#cctor` static-init** flagged `lazy_init_race` —
   impossible by construction (CLR type-init lock); (c) intentional lock-free memoization (has an explicit
   "we don't care if it runs twice" comment). Dedup per method with a count, demote (a)/(b)/(c), and make
   severity track real impact.

3. **Effect identity must be the same across commands, or the documented cross-checks silently fail.** `[tool]`/`[doc]` — *Sev: high (B).*
   The `~ amplified [review]` items in `impact` are keyed by **field** (`F:Echo.ProcessFlags.Default`), but
   `tree --view effects` labels the same shared-state effect by **receiver type** (`ConcurrentDictionary<…>`)
   — so the verification command the docs recommend returns **zero matches** in both base and head. This
   undermines trust in the one finding class explicitly tagged `[review]`. Fix: align the keys, or have the
   `impact` line print the exact ready-to-run verification command for that effect.

### P2 — Composability: vocabulary & machine-output contract

4. **`callers` counts the matched target as its own caller.** `[tool]` `[cheap]` — *Sev: med-high (D).*
   `callers "FactPathFinder.BuildTree"` reports "12 methods reach the target", but 4 of those depth-0 rows
   ARE the target + its lambdas. Strip matched nodes, or separate them under a "Matched nodes" vs "Upstream
   callers" header. Most disorienting single moment in D's session.

5. **Unify the `--roots` / `--orphans` / "Entry-point candidates" vocabulary.** `[tool-labels]`/`[doc]` `[cheap]` — *Sev: med (D).*
   One concept, three names (flag `--roots`, alias `--orphans`, output header "Entry-point candidates"). And
   `--entrypoints` *sounds* more authoritative but is actually **narrower** (rule-gated; misses benchmark/test
   origins `--roots` finds) — users get the super/subset relationship backwards. Pick one term; in `--help`
   contrast them in one line: "`--roots` = heuristic (all no-predecessor); `--entrypoints` = precise (rule-
   matched only, may miss test/bench origins)."

6. **TSV is an API — give it headers, a fan-out column, and a documented per-view schema.** `[tool]`+`[doc]` — *Sev: med-high (A, B, F).*
   - A: the pretty `reaches` view has a labeled "dispatch fan-out (NOT a real call)" bucket, but in `--format
     tsv` the fan-out rows are **interleaved** with real rows, distinguishable only by a non-empty 8th column;
     A nearly counted 7 CHA-fanned writes as guaranteed. Add an explicit `isFanout`/`kind` column or `--no-fanout`.
   - B: `impact --format tsv`'s `structural_summary` row is **six unlabeled integers**. Add a `# header` row.
   - F: `--format llm` is **6 columns** for `--view paths/full` but **7** (adds `parent`) for `--view effects`
     — undocumented; a parser told "6-col" misparses. Document the per-view schema in `--help` + REFERENCE.

7. **Document the LLM-format semantics that shift between views/formats.** `[doc]` `[cheap]` — *Sev: med (F).*
   - `seen` (bare) in `--format llm` vs `seen:<id>` (back-ref) in `llm-ids` — a parser switching formats breaks.
   - `parent_id` means *call-graph parent* in `paths/full` but *nearest effectful ancestor* in `effects` — a
     silent semantic shift that is a correctness hazard for LLM reasoning. Document, or rename the column to
     `effectful_parent_id` in the effects view.
   - In `--view effects`, the `depth` column is the original-tree depth (non-contiguous) — note that structure
     must be read from `parent`/`parent_id`, not `depth`.

### P3 — Noise → signal (the answer exists but is buried)

8. **Give every flood-prone view a triage lever.** `[tool]` — *Sev: med (A, C, E).*
   - `reaches` on a 4352-method EP dumps 752 effects, 483 of them `throw raise`. Lead with a writes/sinks
     rollup, or default `--exclude throw` on the summary.
   - `tree --view hazards` buries the actually-useful deduped footer at the bottom of a 112-line tree — add
     `--summary-only` (footer only), and a `--first-party` / `--exclude-namespace` to drop the framework
     hazards (Echo/ConfigurationManager) that reach every EP. Consider a first-class `rig hazards [--kind]
     [--severity] [--min-count] [--first-party]` command (today hazards are only reachable via TSV grep).

9. **Shorten DocIDs by default in `path`/`callers`/`tree`; put `[invocation @ file:line]` on its own line.** `[tool]` `[cheap]` — *Sev: med (D, A).*
   Full DocIDs with 20+ params line-wrap unreadably and bury the file:line annotation at the end. Default to
   `Type.Method(…)`; add `--full-ids` for scripting.

10. **A combined "inventory" view: `provider:op  Type.Method  file:line  [fanout]` in one command.** `[tool]` — *Sev: med (A).*
    Today A had to run `tree --view effects` (names, no line) AND `reaches --format tsv` (line, verbose) and
    cross-reference. A `reaches --format inventory` would be the ideal compliance/audit artifact.

11. **`symbols`: add `--limit`, a `--no-lambdas` filter, and print the total when truncated.** `[tool]` `[cheap]` — *Sev: low-med (D).*
    `symbols "TreeCommand.RunAsync"` returned 44 rows (1 method + 43 lambdas); `--kind method` truncates at
    "(50 shown)" with no total. Show "(showing 50 of 147)".

### P4 — First-run discoverability

12. **Explain the cwd dependency + a "no store found" message + a quick-start.** `[doc]`/`[tool]` `[cheap]` — *Sev: high for newcomers (E).*
    Nothing in top-level `--help` says "run query commands from the directory containing `.rig/`" — the first
    thing a newcomer does fails confusingly. Add that line, make the no-store error say how to fix it, and add
    a quick-start block (`rig runs` → `rig entrypoints` → `rig tree <EP> --view summary`) or a `rig status`
    health-check command.

13. **Silent empty commands should print "0 …" + a hint, not nothing.** `[tool]` `[cheap]` — *Sev: med (E).*
    `rig di` prints just a heading; `rig files` (no flag) prints nothing and exits 0. Both read as broken.
    Print `0 DI registrations (indexed from XML DI during \`rig index\`)` / `47 indexed files (3 skipped — use
    --skipped)`.

14. **`rig runs` should hint diffable base/head pairs.** `[tool]` — *Sev: med (B).*
    B had to reverse-engineer which of 9 stores form a valid pair from symbol counts + solution paths (and
    dodge an odd-scope store). Group by solution tree, flag odd-scope stores, and/or print a base-candidate
    hint (e.g. the git merge-base).

15. **`entrypoints` output: separate the queryable name from the file path.** `[tool]`/`[doc]` `[cheap]` — *Sev: med (E, A).*
    Newcomers copy the whole EP line and the file-path column (or the `▶` route form) doesn't match anything.
    Bracket/dim the path, or add "Tip: query the dotted name, not the route or path."

16. **Add `--list-providers` (and the warn-on-unknown-token from P1).** `[tool]` `[cheap]` — *Sev: med (A).*
    `--only`/`--exclude` tokens are pure guesswork without the live provider set.

### P5 — Detector & label precision

17. **`IO.TextWriter` shouldn't carry the 📁 (file) glyph for stdout writes.** `[tool]` `[cheap]` — *Sev: low-med (E).*
    `derive`'s own `io:write IO.TextWriter ×16` is just stdout, but the folder emoji implies a filesystem
    sink. Distinguish `console:write` from `io:write IO.File`.

18. **Missing detector families: audit/log, outbound webhook.** `[tool]` `[design]` — *Sev: med (A, C).*
    The headline compliance task ("every DB write preceded by an audit-log call") is unanswerable — rig models
    `llblgen:write` beautifully but can't see the `auditLogEvent.Log()` beside it. At minimum the docs should
    say audit/logging/webhooks aren't modeled. (Note: `unserializable_payload` is documented but didn't fire
    on the MedDBase store — verify the rule still matches.)

19. **`--maxnodes` knob for the node-budget cap.** `[tool]` `[cheap]` — *Sev: low (F).*
    `budget-capped` can appear in output, but the only user lever is `--maxdepth` (the wrong dimension — the
    problem is node count, not depth).

20. **Label the units difference between `--view summary` and `--view effects`.** `[doc]`/`[tool]` `[cheap]` — *Sev: low (E, F).*
    Summary counts call-SITES, effects counts effectful METHODS; the two numbers differ with no explanation.
    Print "81 call-site(s) across 37 effectful method(s)". Also: the root node's `calls=1` is a meaningless
    sentinel in llm output — emit `0`/empty for roots.

---

## What works well (keep-doing — consistently praised, unprompted)

- **`rig path` is best-in-class** (every agent that used it). Annotated traces with `[loop foreach: var @ file:line]`
  and `[impl-dispatch]` per hop turned scary findings benign (A: a "write" was a lazy-provision inside a read;
  C: confirmed a race is reachable from a background worker). Exit code 1 + clear message on "No path" makes it
  scriptable (D).
- **The dispatch fan-out "NOT a real call" disclosure** (A) — honest CHA over-approximation framing that
  repeatedly saved agents from massively over-reporting.
- **The per-effect evidence + loop annotations** — the hazard `evidence` column (`item in items`, dual-write's
  two file:lines) and `🔁xN [loop: var]` are "the right altitude of evidence" (A, C).
- **`--only provider:op` filtering** collapses hundreds of effects to the handful that matter in one flag (A, E).
- **`callers --entrypoints`** precisely answers "which real entry points touch this" with service attribution
  `⟦Service (kind)⟧` (A, D, E).
- **`rig refs`** is the standout for refactor-safety — grouped by overload, file:line per site (D).
- **Churn-immunity of `impact`** — ignored 1788 deleted lines of SOAP scaffold, reported only the 6
  behaviorally-affected EPs; the SOAP→REST diff was correct against `git diff --stat` (B). Both-refs-required
  prevents a silent-wrong-base footgun; the `--expect-no-effect-change` CI gate works.
- **LLM-format token reduction is real** — 6–10× smaller than the full TSV; `seen:<id>` back-ref makes
  redundancy analysis a mechanical `grep | sort | uniq -c`; `--suppress ctors,lambdas` cuts ~22% with zero
  effect loss; the `--view paths` depth+DFS-order invariant lets the tree be reconstructed without id lookup (F).
- **`rig runs` as a health-check** and **`--view summary` as a calibration step** are the right first moves (E, F).
- **Forgiving case-insensitive substring patterns** — `"Type.Method"` just works, no full DocID needed (every agent).

---

## Suggested next actions

The **cheap wins** (labels/headers/docs/filters) cluster in P2/P4/P5 and would remove most of the day-one
friction: callers self-match (#4), the vocabulary unification (#5), TSV headers + LLM schema docs (#6/#7),
the cwd/quick-start/empty-command fixes (#12/#13), `--list-providers` + token warnings (#16/#1a),
DocID shortening (#9), and the `IO.TextWriter` glyph (#17). The **design work** worth scheduling
deliberately is the silent-incompleteness signaling (#1), hazard triage/dedup + detector precision (#2),
and cross-command effect-identity alignment (#3) — these are the trust issues, and trust is the thing the
panel was most willing to withhold.
