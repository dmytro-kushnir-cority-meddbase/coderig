# `rig impact` per-EP footprint drops effects on large EPs (reported as dispatch-order instability)

**Status:** ✅ **RESOLVED** by the node-cap fix (commit `1c70c5ad`,
[impact-reach-walks-silent-maxnodes-cap.md](impact-reach-walks-silent-maxnodes-cap.md)). The reported
symptom was a manifestation of the silent 20k-node cap (B), **not** a separate dispatch-narrowing bug. The
original root-cause hypothesis below (traversal-order instability in `receiverOf`/`viaDispatchOf`) was
**disproven for this repro** — see "Corrected diagnosis". A genuinely-latent order-dependence does exist in
the same code but has **no observed symptom** post-fix; it's recorded at the end, deliberately NOT fixed.
**Repro DB:** `C:\Git\meddbase-analysis` — `--base 49e453cc15a4-dirty --head b522241f89ab-dirty` (main vs
MR 10840), `--async --rules rig.rules.json`.
**Reported by:** user triage, 2026-07-05 (third of a set). Verified + corrected same day.

---

## Symptom (as reported)

In `rig impact --async`, `AppStartupProcesses.Startup` showed `effects 5121→5120, +3/-4` — the 3 real MR
(GK_COMMENTS) additions plus **4 spurious `−`** on payment code the MR never touches
(`PayTypeEntityBase.InitClassFetch` llblgen-read + throw, `EnumExtensions.GetDescriptionOrNames`
shared_state-mutate, `PayTypeRow.get_PkPayType` throw). `rig path` found an identical 15-node path to the
"removed" effect in BOTH stores, so the `−` were false.

## Corrected diagnosis — it was the node cap (B), verified

The report ruled out the node budget because `rig reaches --async "…Startup"` reports **17223 / 17231**
reachable methods (< 20000). That reasoning was the trap: `reaches` uses `ReachesWithFanout`, but the per-EP
**footprint** walk uses `ReachesInfoFromEachSeed`, whose `info.Count < maxNodes` budget the footprint walk
actually exhausts on this EP — the reachable-method count from `reaches` did **not** reflect the footprint
walk's budget usage. Captured ground truth, same store pair, `--async --no-cache`, toggling only the cap:

| Binary | `Startup` `ep_delta` | store-wide `ep_effect_removed` |
|---|---|---|
| **pre-fix** (maxNodes=20000) | `5120 5121 +3 **-4**` | 5 |
| **post-fix** (maxNodes=int.MaxValue) | `10220 10217 +3 **-0**` | 0 |

Removing the cap **doubled** `Startup`'s footprint effect count (5121 → 10220) — direct proof its walk was
truncated — and dropped the 4 spurious `−` (and every other spurious `−` store-wide: 5 → 0). The
order-sensitivity the report observed (unrelated MR methods flipping the deltas) was the **cap truncating at
an order-dependent frontier**: which nodes fall under the 20k line depends on BFS visitation order, so adding
~11 methods elsewhere reshuffled the truncation set. That is bug B, end to end.

**Conclusion:** no `FactPathFinder` / dispatch-narrowing change was made or needed. The fix is B's
`maxNodes: int.MaxValue` on the three impact walks.

## Residual (latent, no observed symptom — deliberately NOT fixed)

The report's mechanism claim is nonetheless *structurally* real: in `ReachesWithFanoutCore`
(`FactPathFinder.cs`), `bindingOf` reaches a fixpoint (set-union + re-enqueue on growth) but `receiverOf`
(narrowing receiver) and `viaDispatchOf` (one-hop dispatch gate) are **BFS-first-reach-wins** (`:469`,
`:472`) with no fixpoint. In principle an unrelated graph change could reshuffle first-reach and flip
receiver-narrowing / dispatch-gating at a node near the imprecise-dispatch frontier.

But post-B there is **zero** observed impact: store-wide async `ep_effect_removed` = 0 across 244+ behavioral
EPs on a dispatch/handoff-heavy MR — the diff is order-stable. So this stays latent and unfixed, because:
- No failing repro exists to validate a fix against — and this is the **trust-critical shared traversal core**
  (`reaches`/`tree`/`callers`/`impact`/`dead` all ride it). Changing dispatch narrowing speculatively, with no
  fixture proving it fixes a real case, is exactly the unvalidated on-by-default change the repo's discipline
  forbids ("ship each detector with a bug/fix fixture"; "FP-calibrate on the real store first").
- The repo's stated philosophy is to **disclose residual CHA imprecision, not add more bespoke narrowing**;
  the principled ceiling is a real type-flow pass.

**If a real order-dependent delta is ever reproduced**, the fix is to give both states `bindingOf`'s
discipline: `viaDispatchOf` as a boolean meet ("false wins" — a node reached by any non-dispatch real call is
re-dispatchable; re-enqueue on flip true→false), and `receiverOf` as a **set** of receivers narrowed by their
union (mirroring `NarrowByTypeArguments`, which already unions over a set of roots), re-enqueued on growth.
Gate that work on first building a synthetic two-store fixture that actually exhibits the order-dependent drop
(a node reachable via both a dispatch edge and a real call whose first-reach differs between two graphs).
