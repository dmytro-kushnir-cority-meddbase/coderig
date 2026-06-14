# Recall audit — `Master.SubmitToHealthcode`

## Entry point
`M:MedDBase.Application.Workflows.InvoiceDebtChase.Master.SubmitToHealthcode(MedDBase.Application.Workflows.InvoiceDebtChase.Controller)` — `InvoiceDebtChase/Healthcode/Master_HealthcodeServiceImpl.cs:991`. Healthcode claim submission: external SOAP, an Echo/object_store queue (`ProcessHealthcodeQueue`), entity-save dispatch.

## rig reported
235 direct effects (+370 dispatch fan-out). Notable: 65 throw, 41 llblgen fetch, 31 entity_cache read, 27 llblgen read, 17 llblgen write, 10 object_store write, 9/8 lock acquire/release, 5 llblgen delete, 4 object_store read, 3 echo_publish ask, 2 tx_rollback/commit, 2 inproc_timer, 2 io write (Smtp temp files), 1 soap submit (`HCWebServices.submitBill`, d0 ✓), 1 smtp, 1 http. Primary effects correctly attributed at d0–d3.

## Confirmed misses
**1. ExportQueue cross-project call edges dropped (io:write invisible).**
- What: `SubmitToHealthcode → ExportQueue.BuildUniqueMessageFilename → Directory.CreateDirectory` (conditional XML payload write) is invisible. `XmlDocument.Save(path)` likewise.
- Source: `Master_HealthcodeServiceImpl.cs:1043-1046` (`if AppSettings[...WriteToFile]=="true" { messageDocument.Save(ExportQueue.BuildUniqueMessageFilename(ExportQueue.GetDestinationPath(...))); }`); `MedDBase.Application.Core.Background/ExportQueue.cs:76` (`Directory.CreateDirectory(destinationPath)`).
- Repro: `rig path "Master.SubmitToHealthcode" "ExportQueue.BuildUniqueMessageFilename"` → "No path"; `rig callers "ExportQueue.BuildUniqueMessageFilename"` → "No symbol".
- Why: call edges from `Workflows` → `Core.Background` are absent from the mined graph despite a valid ProjectReference — a scoped-mine cross-project stitch gap (`Core.Background` netstandard project not call-graph-stitched to the `Workflows` assembly).

## Boundaries (expected)
- `Chamber.Async.OnWorkflowControllerSaved` async event bus (delegate registration) — uncrossable.
- `ConfigurationManager.AppSettings[...]` runtime config gates — no config-read rule (intentional).
- `XmlDocument.Save` was uncovered by io rules at audit time (now added to builtin-rules.json).
- Echo `.tell`/`.ask` actor boundary — correctly tagged.

## Verdict
Mostly trustworthy. Single true bug: the ExportQueue cross-project stitch gap silently omits a conditional file-system write. Fix: ensure the scoped mine stitches `MedDBase.Application.Core.Background` into the same compilation context as `MedDBase.Application.Workflows`.
