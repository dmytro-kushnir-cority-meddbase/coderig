---
name: bug-triage-scan
description: Resumable backward scan of GitLab (git.meddbase.com) bugs, triaging each into a rig detector category (FR-1..FR-11 / out-of-scope / new-candidate). Walks closed Severity bug issues newest→oldest from a cutoff (the candidate spine), using reverts/hotfixes — including orphan direct-to-main commits — only as a confidence HINT, never a filter. The cursor is the frontier block in the committed scan doc, so each run resumes where the last commit left off. Use when the user wants to scan/triage MedDBase bugs for detector coverage, mine reverts/defects into the RCA corpus, advance or resume the bug-triage scan, or asks about the frontier/cursor/scan doc.
---

# bug-triage-scan

Turn the hand-mined RCA corpus into a **repeatable, resumable** scan: walk MedDBase
GitLab bugs backward in time and triage each into a `rig` detector category. Each run
is **one batch** — it reads the cursor from the last commit, harvests the next slice of
older items, triages them, and commits (advancing the cursor). Stop and resume any time.

**Taxonomy + grounds** live in `meddbase-analysis/docs/backlog-bug-detection.md` (FR-1..FR-11,
tiers) and `rca-corpus-meddbase.md` (the curated gold corpus). This skill's OUTPUT is the
high-volume sweep doc `meddbase-analysis/docs/bug-triage-scan.md`; promote high-confidence
finds into the curated corpus by hand.

## The cursor model (read first)

- **Cutoff** — the newest boundary, set ONCE when a scan starts (default: today). The scan
  never looks newer than this.
- **Backward** — candidates are walked newest→oldest by `closed_at`.
- **ONE candidate spine, ONE cursor: closed `Severity::*` bug issues.** The defect IS the unit
  (the corpus's marquee cases #2892/#2930/#1646 are issues).
- **Revert-presence is a HINT, never a filter.** A revert/hotfix — whether a revert *MR* or an
  orphan **direct-to-main commit** (no MR) — is strong "it shipped and was backed out" evidence,
  not a gate. The `reverts` signal index (whole-fetched each run, no cursor) is used to (1)
  annotate an issue candidate with `reverted:` and boost its confidence, and (2) **catch-net** any
  revert/hotfix whose culprit issue is NOT in the Severity spine — triage it anyway.
- **Cursor = the frontier block in the committed scan doc.** There is NO separate state file.
  Resume reads `git show HEAD:docs/bug-triage-scan.md`, parses the block, and asks GitLab for the
  next issues strictly OLDER than the frontier. Re-running with no new commit re-reads the same
  frontier → same place (idempotent).
- **Commit closes the loop** — appending entries + rewriting the block + committing IS both the
  deliverable and the next run's resume point. Each batch = one commit.

## Per-run loop

1. **Resume.** `cd c:/git/meddbase-analysis`. Read `docs/bug-triage-scan.md`; parse the
   ```scan-state``` block (cutoff, the issue frontier, `seen` ref list, batch_size). On a fresh
   doc the seed block already has `cutoff` set and frontier = cutoff. Extract `seen` to
   `scratch/seen.json` for the harvester.
2. **Harvest.** From the skill dir, list the next batch of issues + fetch the revert signal index:
   ```
   python harvest.py list    --project 9 --before-issue <frontier_closed_at> \
     --batch <batch_size> --seen scratch/seen.json
   python harvest.py reverts --project 9        # whole signal index (cache for the run)
   ```
   `list` returns `{issues:[...]}` — the candidate spine. If empty, the scan reached the end of
   history older than the cutoff → report "scan complete", stop (no commit). `reverts` returns
   `{index, by_issue}` — use `by_issue["#NNNN"]` to annotate, and triage any `index` entry whose
   `culprit_issue` is null or not Severity-labeled as a **catch-net** candidate.
3. **Enrich + probe — DO NOT classify off a title.** For each candidate, pull full detail:
   ```
   python harvest.py show --project 9 --issue <iid>   # or --mr <iid> for a catch-net revert
   ```
   This is a HARD RULE: the corpus overturned #4444 (logic, not race) and !10208 (a fix, not a
   regression) only after reading descriptions/notes/diffs. Read the description, human notes,
   `related_mrs`/`closes_issues`, and the diff before asserting a cause. Separate the **trigger**
   (what made it surface — e.g. an accidental live deploy) from the **cause** (the mechanism).
4. **Cross-link dedup.** If a candidate issue's `related_mrs` (or a catch-net MR's `closes_issues`)
   names another candidate, triage them as ONE entry and add BOTH refs to `seen`. Record any
   revert hit from `by_issue` as the entry's `reverted:` evidence (a confidence boost, not a gate).
5. **Classify.** Write one triage record per item (schema below). Route into the fixed taxonomy.
6. **(Optional) rig-ground.** Only for items whose `rig signal` is `already-detects` or a shipped/
   tier-1 detector AND that name an identifiable culprit method. Default OFF per batch (keeps the
   sweep cheap). When on, run from `c:/git/meddbase-analysis`: `rig derive`/`rig tree <m> --view
   hazards`/`rig reaches <m>`, or `rig impact --base <fix>^ --head <fix>`, and record whether the
   detector actually fires. See the `rig` skill.
7. **Commit.** Append the new entries (newest-first within the batch). Advance the issue frontier
   to the **oldest issue touched** this run. Append every triaged ref to `seen` (catch-net MR refs
   too). Bump `batches_run`. Then:
   ```
   git -C c:/git/meddbase-analysis add docs/bug-triage-scan.md
   git -C c:/git/meddbase-analysis commit -m "bug-triage-scan: batch N (<oldest-date>)"
   ```
   One commit per batch. Do not let batches stack uncommitted — the commit IS the cursor.
8. **Fold findings into the reviewer checklist — EVERY batch.** Review the batch for material that
   strengthens [`reviewer-pitfalls.md`](../../../../git/meddbase-analysis/docs/reviewer-pitfalls.md)
   (the LLM-reviewer checklist this scan exists to feed): a **new failure-mode pattern** not yet a
   numbered item, a **stronger/clearer example** than one already cited, or a **new sub-flavor** of an
   existing item ([LLM] logic pitfall or [rig] hazard). Update the relevant item(s) — keep it a tight
   checklist, not a changelog — and commit it in the SAME batch commit (or a paired `docs(reviewer-pitfalls)`
   commit). If a batch surfaces nothing new, say so explicitly; do not invent additions. (Batch 3 added the
   "stale in-memory copy / wrong-field read" item and the FR-4 mis-ordered/branch-divergent sub-flavors —
   that's the bar for "worth adding".)
9. **Reconcile — verify the agents didn't drop work.** Before committing, confirm each chunk emitted a
   record for every worklist ref (`diff` the `### #<iid>` headers against the chunk JSON). A batch-3 agent
   silently emitted 18 of 20; the dropped two were only recovered by this check. Re-triage any missing refs
   inline and add them.

## Frontier block schema

A single fenced ```scan-state``` block at the TOP of the scan doc. Machine-readable; this is
the cursor (one frontier — the issue spine). Keep `seen` to refs (`!iid` / `#iid`) only.

```scan-state
cutoff:      2026-06-26
batch_size:  25
issue_frontier_closed_at: "2026-06-26T00:00:00Z"
batches_run: 0
seen: []
```

## Triage record schema (one per item, newest-first under `## Entries`)

```
### !10418 — Data Import broke: missing Import Instance Id on save · merged 2026-04-29
- **url:** https://git.meddbase.com/mms/meddbase-main-application/-/merge_requests/10418
- **class:** effect-loss regression (a write no longer happens) · **FR:** FR-4 · **confidence:** ✔verified
- **mechanism:** "my previous refactor (!10281) broke the Data Import … I missed updating the Import Instance Id upon saving" — a required write dropped on the import workflow path.
- **trigger≠cause:** symptom = import fails to complete; cause = removed write, not logic.
- **reverted:** no (fix-forward via !10418) — *(if reverted: `!MR` or `<sha>` from the signal index; a confidence boost, never a gate)*
- **rig signal:** already-detects (per-EP `-effect` via `impact --per-ep`) · **rig-checkable:** yes (deferred)
- **closes/links:** #4088 ← !10281 (culprit)
```

### Fields
- **FR** — one of: `FR-1`(+a/b/c/d/e/f/g) shared-state race · `FR-2` AsyncLocal/ThreadStatic ·
  `FR-3` N+1 · `FR-4` effect-set diff · `FR-5` contention · `FR-6` unserializable payload ·
  `FR-7` cache invalidation · `FR-8` dual-write · `FR-9` fire-and-forget-with-effects ·
  `FR-10` event/cascade cycle · `FR-11` retry-in-transaction · `out-of-scope` · `new-candidate`.
- **confidence** — `✔verified` (cause stated in issue/MR/notes/diff, quoted) · `~inferred`
  (plausible mechanism from partial evidence) · `?unknown` (outcome known, cause unrecorded —
  assert NO mechanism).
- **rig signal** — `already-detects` / `cheap-rule` / `new-detector` / `out-of-scope`.
- **rig-checkable** — `yes` / `no` / `yes (deferred)` — whether a culprit method + commit is
  identifiable for a grounding pass.

## Hard rules

- **Probe before asserting** (step 3). Titles mislead; the symptom is not the cause. `duplicate`
  rows can be a race OR plain insert-vs-update logic — only the description/diff routes it.
- **Trigger ≠ cause.** A revert can be triggered operationally (bad deploy) while the code defect
  is latent and real (or absent). Record both; tag confidence on the CAUSE.
- **One commit per batch; advance frontiers to the oldest touched per stream.** Never edit the
  frontier block by guesswork — it is the cursor.
- **`seen` is the boundary guarantee.** Timestamps can tie; refs can't. Always add every triaged
  ref (both sides of a cross-link) to `seen`.
- **No GitLab writes.** Read-only scan. Do not label/comment/close anything on the tracker.

## Files & facts

- **harvest.py** (this dir) — stateless glab harvester. `list` (next issue batch) + `reverts`
  (the signal index, incl. orphan direct-to-main commits via the local clone) + `show` (one item's
  full detail). Project id **9** = `mms/meddbase-main-application` on `git.meddbase.com` (glab authed).
- **Scan doc:** `c:/git/meddbase-analysis/docs/bug-triage-scan.md` (frontier block + entries).
- **Reviewer checklist (maintain every batch, step 8):** `c:/git/meddbase-analysis/docs/reviewer-pitfalls.md`
  — the LLM-reviewer failure-mode checklist this scan feeds; `[LLM]` logic pitfalls + `[rig]` hazards.
- **Taxonomy:** `c:/git/meddbase-analysis/docs/backlog-bug-detection.md` · **gold corpus:**
  `rca-corpus-meddbase.md`. Run any `rig` grounding from `c:/git/meddbase-analysis`.
- Candidate spine: closed `Severity::{Critical,Major,Average,Minor}` issues. Signal index (hint):
  `Revert`/`broke`/`regression`/`hotfix`-titled MRs + orphan revert/hotfix commits on `main`
  (clone `c:/git/meddbase-main-application`). Adjust the constants in `harvest.py` to widen.
