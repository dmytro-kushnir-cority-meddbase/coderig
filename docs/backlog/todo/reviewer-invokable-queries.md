# Reviewer-invokable queries — rig grounds the reviewer's whole-program questions

**Status:** todo · **Source:** concurrent bug-triage-scan, 500-issue MedDBase corpus (5 batches, 2024-01→2026-06), 2026-06-26
**Frame:** ship hazards as **reviewer-invokable queries** (rig answers a question posed about a specific diff),
not only as whole-store scans. **The reviewer poses; rig grounds.**

## The mandate (why this is the payoff)
Division of labor the corpus makes obvious: the LLM reviewer owns **local semantics** (is this predicate/
encoding/logic right — ~87% of defects, rig is blind to all). rig owns the **whole-program** questions a
diff-local reviewer literally can't answer: what else does this reach, does the sibling path do the same, does
any EP reach this write without the guard, did a "behavior-preserving" refactor change the effect set.
**65 of 500 (~13%) sit in rig's reach**, and the rate *rose* going backward (5→7→10→22→21) as the stream
shifted from UX polish to backend/import churn. The **dominant, stable signal across every window is
effect/guard divergence across paths** — which 1–4 below let an LLM reviewer check.

## Ranked by leverage
1. **`rig parity <epA> <epB>` — peer EP effect + guard-set diff. Highest value.** Symmetric difference of two
   EPs' reachable **effects AND guards/asserts on the path** (UI vs EAPI, manual vs import, add vs edit,
   save vs save-as). Serves the dominant ~35-of-65 pattern "two paths to one write, the set differs": FR-8
   import-vs-manual (#557/#766/#775/#1542/#1548/#558), guard-divergent (#1718/#1742), branch-divergent
   (#1254/#1537/#1238/#763). **⚠ EXTENDS the already-shipped `rig effects-diff <a> <b>`** (effect-set diff for
   two EPs is done — see done/effects-diff.md); parity = effects-diff **+ the guard/assert set on the path**.
   So build = add guard/assert capture to the existing diff, not a new command from scratch. (`impact` diffs
   one EP across commits; this diffs two EPs at one commit — a new axis.)
2. **`rig peers <ep>` — sibling discovery.** The reviewer's hard part is *knowing which* parallel path to
   compare. Given an EP, surface peers: other EPs writing the same table/entity, the import/bulk counterpart
   of a UI action, add/edit pairs. Turns the corpus's #1 meta-heuristic ("find the second path the change
   didn't touch") from guesswork into a query. Novel, high-impact. **Feeds parity.**
3. **`rig reaches <effect> --without-guard <pred>` — guard-on-path query.** "Which EPs reach this write
   without passing through guard G (merged-patient check / deleted-status filter / AssertRight)?" Serves
   guard-divergent + authz-before-write (#1718, #1742, #290, #851/#852). Needs modeling a few call shapes
   (AssertRight, IsNone, status checks) as guards.
4. **`rig assert` — policy gates the reviewer authors.** Bridge from "reviewer spotted an invariant" to "rig
   enforces it in CI forever": `assert every-ep-reaching(RecallEntity.Save) also-reaches(ActivityLog.Write)`
   (#56/#1271/#831), `assert no-path(<EP> → object_store:write of Option<T>)` (#1646),
   `assert no-effect-set-change` (the shipped `--expect-no-effect-change`). The confirmed finding becomes a
   durable regression guard. (Floated earlier as backlog item D6 — the corpus is the mandate.)
5. **Serialization-sink typing — "type X flows into sink Y".** Flag a serializer-unsafe value (`Option<T>`,
   Int64→JS, discriminated/delimited) reaching a persist/JS/URL sink. Serves the FR-6 cluster (#1646, #1359,
   #617, #1252, #1781). rig already captures type-args; extend to the value→sink edge.
6. **Transaction-scope facts.** Tag effects "inside the ambient transaction or escapes it" (#1784/#716
   tx-escaping read; #536 throw-in-tx rolls back the intended write; #436 nested-tx) and "wrapped in retry /
   idempotent" (FR-11: #1546/#351/#850). Heavier — needs new extraction-time facts.

## Feasibility tiers (honest)
- **1–4 build on rig's existing reachability+effect graph** — the high-ROI near-term set. (#1 is largely
  effects-diff + guard capture; #3/#4 need a small guard-shape model.)
- **5 extends type-arg capture** (the value→sink edge).
- **6 needs new extraction-time facts.**
- **Out of even rig's extended reach (disclose, don't claim):** swallowed-`Either`-before-a-security-effect
  (#850) and "model field set in code but never mapped to a DB write" (#145) — need value/dataflow rig lacks.

## Shortest path to value
**1 + 2 + 4** — sibling discovery (`peers`) feeds the parity-diff (`parity`, extending `effects-diff`) feeds an
`assert` gate. That trio mechanizes the corpus's single biggest theme (path divergence, ~half of in-scope) and
hands the LLM reviewer a question it can't answer alone **plus** a way to make the answer permanent in CI.

_Companion corpus artifacts live in `meddbase-analysis`: `docs/bug-triage-scan.md` (500 issues, resumable at
2024-01-24), `docs/reviewer-pitfalls.md` (11 `[LLM]` + 8 `[rig]` items)._
