# Handoff — Receiver-type dispatch narrowing (the signal/noise fix)

## Why this matters (read first)
On the MedDBase monolith, `rig tree`/`reaches` blast radius is dominated by **CHA virtual-dispatch
over-approximation**, not real business logic. Concrete: `rig tree ProcessHealthcodeQueue` = **604
effectful methods**, but ~115 of them are `SomethingEntity.Save` reached only because
`CommonEntityBase.Save` is virtual and rig unions **all 114 entity overrides** at every Save call site.
At runtime that site hits exactly **one** (the receiver's type). The depth-5 real surface is ~64 methods;
everything past it is two "god-seams" (`CommonEntityBase.Save`, `WorkflowManager`/`IWorkflowMaster`
registration). **Without receiver narrowing, signal/noise on this codebase is ≈0.** This task makes the
tree itself precise so users stop mentally subtracting the fan-out.

## Root cause (verified, with file:line)
- The receiver's static type **is mined per call site**: `ReferenceFactEntity.ReceiverType`
  (`src/Rig.Storage/Storage/ReferenceFactEntity.cs:14`) and `FactInvocation.ReceiverType`
  (`src/Rig.Domain/Data/Facts.cs:37`), captured at member-access invocation sites.
- The **call graph drops it**: `CallEdge` (`src/Rig.Domain/Data/Facts.cs:93`) carries
  `Caller, Callee, Kind, FilePath, Line, LoopKind, LoopDetail` — **no ReceiverType**. The graph is
  method→method; the call site (and its receiver) is abstracted away.
- Dispatch is therefore resolved **at the target-method node, CHA-style**:
  `FactPathFinder.DispatchTargets(current, index)` (`src/Rig.Domain/Functions/FactPathFinder.cs:~462`)
  — standing at `CommonEntityBase.Save`, it unions every descendant `Save` that `IsOverride` ⇒ 114.
  `Successors(current, index)` (`~431`) yields direct call edges (now line-sorted) then these dispatch
  targets with a fan-out degree. `AllDispatchEdges(graph)` (`~482`) materialises the same, globally,
  for the precomputed `dispatch_edges` table.

Two stacked over-approximations:
1. **Receiver-blind CHA** — virtual `Save()` fans to all 114 overrides regardless of static receiver type.
2. **Base-call blindness** — the seam path is `ReferralEntity.SaveNoSecurity → base.Save()`. `base.Save()`
   is **non-virtual** (resolves to exactly `CommonEntityBase.Save`) yet rig still fans out at that node.

## The fix
Receiver-type narrowing, applied **per call site / per edge** (not per node, which is the whole point):
at a call edge `caller --(receiver R)--> Callee` where `Callee` is a virtual/base method, restrict
override-dispatch to `Callee`'s name on **`TypeClosure.StripGeneric(R)` and R's (transitive) descendants**,
instead of all descendants of `Callee`'s declaring type. `company.Save()` (R=`CompanyEntity`) ⇒
`CompanyEntity.Save` (+ Company subtypes), not 114. Interface (impl-)dispatch gets the same treatment when
R resolves to a concrete type.

### Soundness + fallbacks (preserve recall — this is non-negotiable)
Narrowing is **strictly more precise without dropping real targets**: CHA includes runtime-impossible
targets; narrowing to the receiver's static-type subtree is what the CLR actually dispatches over.
**Fall back to full CHA** (current behaviour) whenever R is unreliable:
- `ReceiverType` is null/missing,
- R is an interface or an error-type (`!:Name`, pervasive under net48 partial binding),
- R is exactly the declaring base type (no narrowing possible),
- R doesn't resolve to a known first-party type in the index.
This keeps rig's deliberate "recall over precision for broken bindings" stance (mirror the existing
`ImplsByErrorInterfaceName` simple-name recovery). Narrowing fires ONLY when R resolves to a concrete
first-party type with known descendants.

### base.M() caveat (investigate)
A `base.Save()` is non-virtual → should resolve to exactly the base method, **no dispatch**. Check whether
`ReceiverType` (or any mined field / RefKind) distinguishes a `base.` access. If it does, suppress dispatch
for base-calls. If it doesn't, document the residual (receiver narrowing alone reduces the 114 but a
self-typed base-call may still narrow to the enclosing type's own override — acceptable interim, but note it).

## CRITICAL landmine — the SQL/in-memory equivalence test
`tests/Rig.Tests/Analysis/SqlReachabilityTests.cs::Sql_depth_reachability_matches_the_in_memory_oracle_both_directions`
asserts `SqlReachability.ReachedWithDepthAsync` (SQL over `call_edges ∪ dispatch_edges`, **CHA**) **equals**
`FactPathFinder.ReachedBy/Reaches` (in-memory). If you narrow in-memory but keep `dispatch_edges` CHA, the
in-memory set becomes a **subset** and this test BREAKS.

Recommended resolution (pragmatic, ship this): **narrow only in the in-memory `FactPathFinder`** (which backs
`tree`/`reaches`/`path` — where the 10-screen pain is) and **keep `dispatch_edges` as the sound CHA superset**
used purely to BOUND the SQL load (`reach_set`). Loading a slightly larger bounded subgraph is harmless —
`FactPathFinder` narrows during traversal and simply won't visit the extra nodes. Then change the equivalence
test from "equal" to **"SQL set ⊇ in-memory narrowed set"** (superset), and add NEW tests asserting narrowing
(below). Do NOT try to make `dispatch_edges` call-site-aware — that breaks the clean From/To edge model and
inflates the table; it's out of scope.

Make narrowing controllable so the test/oracle can still exercise CHA: e.g. narrowing fires when CallEdges
carry receiver types AND a `narrowDispatch` mode is on; provide a CHA mode for the equivalence direction.
Design the toggle however is cleanest, but keep an explicit CHA path for tests.

## Files to touch (verify each)
1. `src/Rig.Domain/Data/Facts.cs` — add `ReceiverType` to `CallEdge` (nullable string).
2. `src/Rig.Storage/Queries/Reads.cs::LoadFactGraphAsync` (~line 139) — select `r.ReceiverType` into the
   `CallEdge` projection (it's on the same `reference_facts` rows already being read).
3. `src/Rig.Storage/Queries/GraphMaterializer.cs` — `call_edges` schema (`EnsureSchemaAsync`) gains a
   `ReceiverType` column; `InsertCallEdgesAsync` writes it; it reads from `FactPathFinder.AllCallEdges`.
4. `src/Rig.Domain/Functions/FactPathFinder.cs::AllCallEdges` (~493) — include `ReceiverType` in the tuple.
5. `src/Rig.Storage/Queries/SqlReachability.cs::LoadGraphFromReachSetAsync` — the bounded-graph load
   reconstructs `CallEdge` from `call_edges`; select + populate `ReceiverType` so the in-memory traversal
   over the bounded graph can narrow. (The reach_set CTE itself stays CHA — don't touch dispatch_edges.)
6. `src/Rig.Domain/Functions/FactPathFinder.cs` — the core change: resolve override/impl-dispatch
   **edge-aware** using the receiver type, with the fallbacks above. `Successors`, `DispatchTargets`,
   and `BuildReverseMaps`/`Predecessors` (reverse dispatch for `callers`) all need the receiver-aware
   variant. Keep `AllDispatchEdges` CHA (it feeds dispatch_edges).
7. Tests — relax the equivalence test to superset; ADD targeted narrowing tests (below).

## Validation (must do before declaring done)
- **Unit/fixture**: add tests in `SqlReachabilityTests`/`FactPathFinderFanoutTests` using the LegacyNet48
  fixture: a virtual/base method with ≥2 overrides + a call site with a concrete receiver ⇒ narrowed
  traversal reaches only the receiver's override, while a call via the base type / interface still fans
  out (CHA fallback). Confirm reverse (`callers`) narrows symmetrically.
- **Real-store smoke** (the headline): `cd C:\Git\meddbase-analysis` then `rig graph` (rebuilds
  `call_edges` WITH ReceiverType + `dispatch_edges` + `nodes` + FTS), then:
  - `rig tree "M:MedDBase.Application.Workflows.InvoiceDebtChase.Master.ProcessHealthcodeQueue" --effects`
    should collapse from **604** toward the real surface (~tens), NOT thousands.
  - `rig reaches "M:…CompanyEntity.Save(SD.LLBLGen.Pro.ORMSupportClasses.IPredicate,System.Boolean)"`
    — the `CommonEntityBase.Save dispatch [fan-out of 114]` bucket should shrink/vanish for concrete
    receivers.
  - Spot-check a known concrete-receiver call still reaches its real override (no lost recall).
- **Full suite**: `dotnet test tests/Rig.Tests/Rig.Tests.csproj -c Release` — all green.

## Build / ship workflow (this repo's conventions)
- Build/iterate: `dotnet build src/Rig.Cli/Rig.Cli.csproj -c Release -v q`. Query with the freshly built
  `src/Rig.Cli/bin/Release/net10.0/Rig.Cli.dll` (run via `dotnet <dll> …`) from the store dir.
- **Format before mini-ci**: `dotnet tool run csharpier format .` (mini-ci runs `csharpier check .` over the
  WHOLE repo and `-warnaserror`; unformatted files fail it). Pinned csharpier 1.2.6.
- Ship + reinstall global tool: `pwsh -NoProfile -File scripts/mini-ci.ps1` (csharpier check → build
  `-warnaserror` → tests → pack → `dotnet tool install --global rig`). Confirm `rig --version` bumps.
- After code change, **re-run `rig graph` on `C:\Git\meddbase-analysis`** so `call_edges` is rebuilt with
  the new `ReceiverType` column (old stores lack it → narrowing must no-op gracefully = CHA fallback).

## Do NOT regress (recent work all shipped in 0.1.1-ci.2026061019…)
- Engine-enforced **read-only** query connections (`RigDbContext(..., readOnly:true)`; temp tables live in
  the writable temp DB — fine).
- **FTS5-trigram** `symbol_fts`/`ref_target_fts` for `symbols`/`refs`; the **`nodes`** seed table; all built
  by `rig graph` (`GraphMaterializer`).
- **Source-order** sort in `Successors` (line-ordered direct edges, dispatch bucketed after) — KEEP; your
  edge-aware dispatch must preserve deterministic source ordering.
- `tree --effects` (compact), per-effect **emoji** (`EffectEmoji`, `rig.effect-emoji.json` override),
  forced **UTF-8** output (`Program.cs`), and the **`--only`/`--exclude`** effect filter (comma/ws list).
- `dead`/`path`/`callers` semantics; the `dead` full-graph load is an intentional TODO (cold path).

## Environment
- Windows, PowerShell. .NET 11 preview SDK; projects target net10.0. Global tool `rig` already installed.
- `C:\Git\meddbase-analysis\.rig\rig.db` (1.7 GB) is the graphed store for the MedDBase app (source under
  its `src/`). The `meddbase-main-application` store is bigger and currently OOMs on `rig graph` — use
  meddbase-analysis for validation.
- vswhere-on-PATH is only needed for AOT publish (not this task).

## Definition of done
Receiver-aware narrowing in the in-memory traversal, ReceiverType plumbed onto CallEdge end-to-end,
CHA fallback for unreliable receivers, equivalence test relaxed to superset + new narrowing tests,
`ProcessHealthcodeQueue --effects` collapsed to the real surface, full suite green, formatted, shipped
via mini-ci with a confirmed version bump, and `rig graph` re-run on meddbase-analysis.
