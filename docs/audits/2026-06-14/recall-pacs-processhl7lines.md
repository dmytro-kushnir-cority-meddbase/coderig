# Recall audit — `InboundServer.ProcessHL7Lines`

## Entry point
`M:MedDBase.PACS.Inbound.InboundServer.ProcessHL7Lines(System.String[])` — `src/pacs/MedDBase.PACS/Inbound/InboundServer.cs:32`. HL7 ingest, routes by sending application to 7 processors.

## rig reported
425 reachable, 35 direct effects (+24 dispatch fan-out): 28 throw, 4 echo_publish tell, 1 io read, 1 object_store delete, 1 object_store write. Dispatch: the string-switch `ProcessMessage` dispatches 6 concrete `Func<HL7Document,Option<IChamberMessage>>` processors — rig follows all 6, plus the generic-file `Interpreter.Interpret`/`Documents.Process` arm. Good dispatch coverage.

## Confirmed misses
**1. `Interpreter.MoveExternalFile` file ops (move/delete/mkdir) — `Try<T>` monad delegate-body gap.**
- `FileExt.Move`/`FileExt.Delete`/`FileExt.CreateMissingFilePathFolders` (all `System.IO.File`/`Directory`) called inside `Documents.cs:337-348`, composed via `LanguageExt.Try<T>` `from…in` (`SelectMany`) chain. `rig reaches "Interpreter.MoveExternalFile"` → 6 methods, **0 effects**.
- Why: the file ops are arguments lifted into `Try<T>.SelectMany` continuation lambdas. rig follows a bare `Try(() => expr)` (the `File.ReadAllBytes` case at line 308 IS seen) but loses the chain across multiple composed `SelectMany` layers — a delegate-consumer body gap triggered by the `Try<T>` monad rather than an explicit `Func<>` arg.

**2. Silent external-assembly boundary — `PreviewRtf.RtfStringToHtml` → `RtfPipe.Rtf.ToHtml`.**
- `Intelerad.cs:34` → `PreviewRtf.cs:11` (`Rtf.ToHtml(rtf)`, external NuGet `RtfPipe.dll`). rig reaches `RtfStringToHtml` (0 effects) and stops with **no `«opaque»` tag** — a reviewer can't distinguish "pure leaf" from "unindexed external library". (Likely pure in practice; the silence is the gap.)

## Boundaries (expected)
Echo `Process.tell<T>` («opaque»); TCP/socket listeners (`ListenForClients`, not on the REST `ProcessHL7Lines` path); no LLBLGen DB effects (processors return `IChamberMessage` Tell-ed to the chamber). Generic `Interpreter.Interpret<T>` over-approximates to both PACS + Pathways interpreters (inflates reach, no false negative).

## Verdict
Partially trustworthy. Primary dispatch + Echo path correct. Top gap: **Try-monad delegate-body gap** hides 3 file writes in `MoveExternalFile`. Secondary: silent external-assembly stop (tag as `«opaque: external assembly»`).
