# Wire `cache_coherence` (+ `event_cycle`) into per-EP `tree --view hazards`

**Status:** todo · **Found:** 2026-06-25 (GI-#4460 trace) · extracted from the session-2026-06-25 findings.

`cache_coherence` and `event_cycle` are whole-program graph-property derivations, surfaced only as
`rig derive` hazard rows — they are NOT in the per-EP `tree <EP> --view hazards` view. So "run the FR-7
detector scoped to an entry point" is impossible today; you reverse from the anchor with
`rig callers <site> --entrypoints`.

**The feature:** filter these findings to anchors reachable from `<EP>` and emit them inline in
`tree <EP> --view hazards` like the effect-attached hazards. Substrate exists — `DeriveCommand` already maps
both into `HazardFinding`; the EP-reach join is the usual `reachable.ContainsKey(site)`.
