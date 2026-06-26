# `rig index --from <csproj>` crashes when the closure resolves to 0 buildable C# projects

**Status:** todo (cheap guard) · **Found:** 2026-06-25 · extracted from the session-2026-06-25 findings.

`rig index --from <csproj>` whose entry-closure resolves to **0 buildable C# projects** throws an unhandled
`System.IndexOutOfRangeException` in `SolutionSourceLoader.LoadAsync` instead of failing cleanly with a
"nothing to index" diagnostic. Add a guard: empty resolved-project set → clear error + non-zero exit, not a
crash. (Surfaced trying `--from ContractManagement.Messages.csproj`.)
