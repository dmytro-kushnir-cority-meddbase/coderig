# Recall audit — `DocumentShareProcess.HandleAuthenticateMessage`

## Entry point
`M:MedDBase.DocumentShareService.Processes.DocumentShareProcess.HandleAuthenticateMessage(AuthenticateMessage, TimeSpan, int, Action<DocumentShareRecord>, Action<DocumentShareRecord>)` — `document-share/Meddbase.DocumentShareService.Processes/DocumentShareProcess.cs:94`. Standalone DocumentShareService message handler.

## rig reported
`reaches`: 11 reachable methods, **1 direct effect (throw)**. `--async`: identical. Actual behaviour: 2 `llblgen:write` + 1 `chamber_msg:tell`. Severe under-report.

## Confirmed misses
**1+2. Delegate-consumer body gap — `DataAccess.SuccessfulLogin` / `DataAccess.FailedLogin` (llblgen:write).**
- The handler receives `onLoginSuccess`/`onLoginFailed` `Action<>` params, invoked at lines 104/101 (`onLoginSuccess(msg.DocShare)`), bound at the inbox (line 46) to `DataAccess.SuccessfulLogin`/`FailedLogin`, each doing `entity.Save()`.
- Repro: `rig path "DocumentShareProcess.HandleAuthenticateMessage" "DataAccess.SuccessfulLogin"` → "No path". Inbox-level `callers` DOES surface the binding (one frame), but it's not propagated into the handler body.
- Why: method-group→delegate binding at the call site is not propagated into the callee body invocation of the delegate parameter (DelegateConsumer body gap).

**3. Same gap, `HandleNewAuthCodeMessage` → `SmsMessaging.SendAuthCode` (chamber_msg) + `DataAccess.SaveNewCode` (llblgen:write)** — delegates invoked two frames deep inside `AuthCode.GenerateNew` (separate class); no static reference at all.

**4. Missing `UpdateMulti` rule — `DataAccess.ExpireAll`/`MergePatient` report 0 effects.**
- `DocumentShareCollection.UpdateMulti(entity, predicate)` (bulk UPDATE) had no llblgen:write rule (only `DeleteMulti`). `DataAccess.cs:98/106`.
- FIXED: `UpdateMulti`/`UpdateMultiAsync` → llblgen:write added to meddbase rig.rules.json; verified `DataAccess.ExpireAll` now reports `d0 llblgen write`.

## Boundaries (expected)
Echo `Process.tell`/`replyIfAsked`/`ask` («opaque: Echo actor framework»); `chamber_msg:tell` via `SendDocShareAuthCodeMsg.Tell`; `RijndaelManaged` crypto; `DFS.Load` correctly tagged `object_store:read` from the inbox.

## Verdict
Two structural gaps: (1) **delegate-consumer body gap** (top) — handler effect set incomplete; workaround = query from the enclosing inbox. (2) **UpdateMulti rule gap** — now fixed.
