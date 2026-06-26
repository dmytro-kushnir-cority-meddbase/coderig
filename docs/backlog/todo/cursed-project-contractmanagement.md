## 🪦 The cursed project — `ContractManagement.Site` won't index (here be dragons)

**Status:** abandoned-for-now · lowest priority · do not feed after midnight.

`rig index ContractManagement.Site.(slnx|csproj) [--merge]` always fails with
`DegradedBuildException: 'ContractManagement.Site' design-time build produced 0 source files after 3
attempts`, which aborts the whole-solution merge. Confirmed **NOT** a build-state problem (4 attempts):
- Has real source (33 `.cs`, 29 `<Compile>` items) — structurally like the WORKING `MedDBase.csproj` (128
  `<Compile>`). So "0 source files" is a DTB *failure*, not an empty project.
- Paket-restored (`.paket/paket.exe restore`, rc=0) + MSBuild-built (rc=0) + **built in Rider** +
  `--no-build-cache` — every combination still yields 0 source files. rig runs its OWN Buildalyzer
  design-time build (not the external build output), so building it elsewhere cannot help.
- Same failure indexing the `.csproj` directly (no `.slnx`, no Echo cross-refs) — rules out a solution-graph
  issue.
⇒ Buildalyzer's design-time MSBuild for THIS net48 ASP.NET web project (`Microsoft.WebApplication.targets`,
`UseIISExpress`, 4 `Echo.Process` project refs) returns an empty compilation. Why this one and not
`MedDBase.Site` (which indexes via the MSBuild-built + `--from <csproj>` pipeline) is unknown — needs
a Buildalyzer **binlog** to see the real DTB error, not another retry. **Irrelevant to `cache_coherence`**
(separate bounded context; doesn't bulk-write the main app's cached entities), so parked. If ever needed:
capture the DTB binlog, or replicate the MedDBase.Site curated pipeline for the contract-management closure.
