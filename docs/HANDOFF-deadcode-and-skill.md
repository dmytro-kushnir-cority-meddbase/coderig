# Handoff — coderig dead-code cleanup + package rig as a Claude skill

> Prepared 2026-06-09 for execution in a fresh session. Two independent tasks. Read
> the memory file `project_coderig_status.md` first — it has the full history,
> the 4 recall fixes, and the validation findings this plan builds on.

---

## 0. Current state (ramp-up)

- **Repo:** `C:\git\coderig`, branch `fact-layer-stage2` — **NOT pushed**. Working tree has
  a few **pre-existing, not-mine** uncommitted changes (`src/Rig.Analysis/Inventory/SolutionSourceLoader.cs`,
  `docs/handoff-coderig-gapfix-mine.md`) — review/leave, don't blindly commit.
- **Global tool:** `rig` = `0.1.1-ci.20260609184821` (commit `e7f7181`). Has all recall fixes
  + `rig tree` + `rig callers`.
- **Build (fast iter):** `dotnet build src/Rig.Cli/Rig.Cli.csproj -c Debug /p:UseSharedCompilation=false`
  → run `dotnet C:\git\coderig\src\Rig.Cli\bin\Debug\net10.0\Rig.Cli.dll <args>` from the dir holding `.rig`.
  (Don't run a `mine` from the Debug dll and rebuild Debug simultaneously — the running tool locks the bin; build `-c Release` to iterate during a mine.)
- **Tests:** `dotnet test tests/Rig.Tests/Rig.Tests.csproj -c Debug /p:UseSharedCompilation=false` → 55 pass / 2 skip.
- **Ship to global:** `powershell -ExecutionPolicy Bypass -File C:\git\coderig\scripts\mini-ci.ps1 -SkipTests`
  (absolute path; NU190x/csharpier warnings are expected and non-fatal).
- **rig in one line:** fact-based .NET code intelligence. `index`/`mine` extract facts → `.rig/rig.db`;
  `derive` (entrypoints+effects), `reaches <m>` (forward effects), `tree <m>` (call tree), `callers <m> [--roots]`
  (reverse reachability; `--roots` = entry-point candidates), `path <a> <b>`, `refs`, `symbols`.
- **Session commits (local, `fact-layer-stage2`):** d670ec8, 7865c0c, 6049997, 8a4bd91, 73d9ae3, e7f7181, 4ecec57, df8ab6a.

---

## TASK 1 — Clean up dead code in coderig (rig-on-coderig self-analysis)

**Goal:** remove genuinely-dead first-party code from the coderig codebase, conservatively, using
rig on itself + C# compiler signals as cross-checks. This doubles as the deferred **Task #7**
(unreachable-symbol finder) and as dogfooding/self-debug of rig's own entry-point derivation.

**Invariant being exploited:** every real first-party symbol should be reachable from some entry point.
Unreachable ones are either dead code *or* expose an entry-point-derivation gap in rig — both worth knowing.

**Recommended approach (conservative, report-then-remove):**
1. **(Optional, higher-value) build the finder** described in Task #7: a `rig dead` / reachability-complement
   = first-party symbols NOT in the union of `reaches` from all derived entry points. Read-only, confidence-tiered.
   `FactPathFinder.ReachedBy`/`EntryRootsReaching` (just added) are the primitives; the complement is
   `all first-party method symbols − ∪ reaches(entrypoint)`. Down-rank: interface/override members, public
   API surface, attribute-decorated, `methodGroup`/handoff-referenced, already-`[Obsolete]`.
2. **Index coderig itself** from a scratch cwd (NOT the meddbase-analysis dir): `rig index <coderig sln or
   src/Rig.Cli/Rig.Cli.csproj>`. coderig is net10/netstandard2.0 — no net48 binding flakiness, should index clean.
   Entry points = `Program.Main` / `CliApplication.Run` + the command dispatch switch + xUnit `[Fact]`s.
3. **Cross-check with the C# compiler / analyzers** (the trustworthy signal): collect CS0169/CS0414 (unused field),
   IDE0051/IDE0052 (unused private member), CS8019 (unnecessary using), unreferenced `internal` members.
   `dotnet build -warnaserror:false` and read warnings; consider a temporary analyzer run.
4. **CAVEATS — never auto-delete on rig alone.** False-positive sources: reflection, DI, the JSON-rule-driven
   detector code (rules reference types/methods by string), public API consumed by tests/external, the
   StructuredOutput/agent surfaces. rig-flagged-unreachable must be compiler- or human-confirmed before removal.
5. **Remove in small commits**, `build` + full `dotnet test` green after each. Branch off `fact-layer-stage2`
   (e.g. `deadcode-cleanup`) if you want isolation.

**Deliverable:** dead code removed in focused commits; tests green; optionally the `rig dead` command shipped
(which closes Task #7 and is reusable on MedDBase next).

---

## TASK 2 — Package rig as a proper Claude skill, install in USER scope

**Goal:** a reusable Claude Code skill encapsulating the rig analysis + validation workflow, so any future
session (any repo) can drive rig without re-deriving the conventions.

**How:**
1. Use the **`write-a-skill`** skill to scaffold (proper SKILL.md frontmatter, progressive disclosure, bundled resources).
2. **Skill scope/content** (proposed name: `rig` or `code-intel`):
   - *When to use:* trace entry-point→effect call graphs; reverse reachability ("who/what entry points reach X");
     effect inventory (DB/cache/object-store/throw/messaging); ground-truth-validate static analysis findings.
   - *Cheatsheet:* `index`/`mine` (+ pre-build & parallelism-1 gotchas), cwd→`.rig` + `--rules` cascade,
     `derive`/`reaches`/`tree`/`callers`/`path`/`refs`/`symbols`, the detector families (entity_cache,
     throw incl. `failwith`, object_store write/delete, llblgen, clientpage, chamber_msg).
   - *Caveats section:* the fundamental limits (Echo.Process actor boundary, reflection/`[ClientAction]`,
     `Activator.CreateInstance`, cross-process asks) and the dispatch-recall fixes (`!:` error-edge fallback,
     generic-stripped lookup) so users interpret results correctly.
   - *Recipe:* the ground-truth validation loop (Sonnet agent traces source → compare to rig → fix detector → commit).
3. **Install in USER scope:** `~/.claude/skills/<name>/SKILL.md` (+ resources). (Project scope is `.claude/skills/`;
   user scope is `~/.claude/skills/` = `C:\Users\dkushnir\.claude\skills\`.)

**Decisions to make at execution time:** exact skill name; docs-only vs. also-automates (e.g. a slash command that
runs index→derive→validate); whether to bundle a copy of `rig.rules.json` as a starter ruleset.

---

## Pointers
- Memory: `MEMORY.md` index; key files `project_coderig_status.md` (history + fixes + validation),
  `project_meddbase_analysis_workflow.md`, `feedback_ground_truth_fixtures.md` (validate against source/fixtures, NOT the mined DB),
  `feedback_coderig_detectors.md` (detectors are data).
- Validation methodology proven this session: pick methods → Sonnet agents trace from source → diff vs rig → fix detectors → commit.
- MedDBase analysis DB (broad mine, throw-aware): `C:\Git\meddbase-analysis\.rig` with tuned `rig.rules.json`.
