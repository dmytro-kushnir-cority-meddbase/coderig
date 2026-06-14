# rig — issue backlog from the MR-!10645 audit review

Distilled from a fresh-Opus audit of MedDBase MR !10645 that drove rig end-to-end and ground-truthed
every claim against source (the source `docs/todo.md` was removed once its findings were actioned).
Priorities: **P2** = cheap coverage win, **P3** = feature.

Convert to GitHub issues with `gh issue create` once `gh` is installed/authed (remote: `dv00d00/coderig`).

> **All P1 correctness bugs from the original audit (A1 `Save()` dispatch fan-out, A2 ctor-fetch+`Save()`
> recall, A3 overload/continuation edge) and the P2 `object_store` read gap (B1) were verified
> NON-REPRODUCIBLE on 2026-06-14 against the current build + store and removed.** Repro evidence:
> `CompanyEntity.Save` vs `SiteEntity.Save` now diverge (1454/73/7 vs 1196/27/5 — fan-out gone);
> `SaveClinicians.SaveClinician` now surfaces the fetch+write (6 write / 4 fetch); the `AssertRight`
> path resolves; `object_store read` fires 147×. The `ctor_fetch` rule-tightening idea (closed A2) is
> likewise moot.

---

## Findings from the 2026-06-14 entry-point audits (8 EPs + comms sweep)

Full reports under [audits/2026-06-14/](audits/2026-06-14/). Eight Sonnet audits (5 forward recall,
3 reverse EP-discovery) + a cross-service comms inventory, all ground-truthed against MedDBase source.

### DONE — trivial effect-rule fixes (applied + verified 2026-06-14)
- **Flurl HTTP** (`Flurl.Http.GeneratedExtensions` GET/POST/mutate) → `http` — closed a codebase-wide blind spot (Medicare PRODA + verification, SignatureRx, Opayo, …). `builtin-rules.json`. Verified: `MedicareVerification.VerifyPatient` reports `http POST`. NB `resource: "declaring_type"` — the fluent URL/receiver isn't statically minable, so `http_argument`/`receiver_type` dropped the effect.
- **WebClient** (`System.Net.WebClient` download/upload) → `http`. `builtin-rules.json`. Verified on `PhysioTecAPI.GetPhysioTecAuthToken`.
- **`XmlDocument.Save` / `XDocument.Save`** → `io:write`. `builtin-rules.json`. Verified on `Mirth.LabChannels.UpdateLab`.
- **LLBLGen `UpdateMulti`/`UpdateMultiAsync`** → `llblgen:write` (mirrors `DeleteMulti`). meddbase `rig.rules.json`. Verified on `DataAccess.ExpireAll`.

### Open findings — triage

| ID | Finding | Root cause | Sev | Effort | Found on |
|----|---------|-----------|-----|--------|----------|
| **F1** | Delegate/lambda **body** not traced through a wrapper/monad → the effect inside is invisible | DelegateConsumer body gap (deeper than the handoff-classification fix) | **High** | High (extractor) | webhooks `httpClient.PostAsync` via `Histogram.Log1Async`; PACS `FileExt.Move/Delete` via `Try<T>.SelectMany`; documentshare `onLoginSuccess`/`saveCode`; `memo()` |
| **F2** | `--entrypoints` misses non-`[ClientAction]` EP kinds | EP-detector rule set incomplete | **Med-High** | Low–Med (rules) | SignalR `HubBase`; Web API `ApiController`+`[Http*]`; ASMX `[WebMethod]`; `Page_Load`; DataServer `ServletBase.Serve/Validate` |
| **F3** | Background detector tags the **wiring** method, not the scheduled **delegate target** | `BackgroundProcessSchedule(…,Callback,…)` target not promoted to EP (Layer-2 handoff) | **Med** | Med | `CheckForZeroDebt`, `ProcessHealthcodeQueue`, `DoDueActions`, `ReferralSLAService.Worker` |
| **F4** | Echo inbox name convention too tight; `IService` interface vs abstract `ServiceBase` FQN | rule keys on exact name `Inbox` / wrong base type | **Med** | Low (rules) | `PathwayInstance.InstanceInbox`; `AppStartupProcesses.Startup` |
| **F5** | Cross-project call edges dropped | scoped-mine stitch gap | **Med** | Med (miner) | `SubmitToHealthcode → ExportQueue.*` (Core.Background not stitched to Workflows) |
| **F6** | Silent stops at boundaries (external assembly, clientpage_proxy, cross-deployment Redis) | no seam tag emitted (= D4) | **Low-Med** | Low | `RtfPipe.Rtf.ToHtml`; Medicare dialog proxy; webhook Redis handoff |
| **F7** | `--roots` precision noise: `P:`/`F:` roots, interface stubs, unpropagated `save:false` guard | reverse-walk heuristic + no constant propagation | **Low** | Low | filterable by prefix |

**Positives confirmed**: the forward `Save()` fan-out fix (retired A1) holds AND reverse base-virtual dispatch is precise (no leak); interface dispatch resolves (`IWebhooks`→concrete); `--roots` recovers ~100% of the EPs `--entrypoints` misses.

**Next trivial slice** (after the DONE rules): **F2 + F4** are low-effort rule additions (new EP kinds + loosen conventions) with high recall payoff. **F1** is the deepest and highest-impact (a tracer change) — schedule deliberately.

---

## C. Detector families (P3 — ordered by audit value)

1. **`entity_save_hooks`** — model `*Entity.Save()` as a typed effect (real lifecycle consequences: `webhook`, `audit:PersonEvent`, `account_resave`, `occ_bump`) instead of relying on dispatch reach alone. Accuracy win for migration blast-radius audits.
2. **`webhook` / `notify` provider** — keyed on the real emit API (`OnCompanyChanged`, `OnSiteModified`, `OnSystemAccountChanged`, ePrescribe publishers). Answers the core R1 question "does this write notify externally?".
3. **`permission_assert` / `rights_gate` provider** — detect `CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight/HasRight` and `*Cache.IfCanView`, carrying the `Rights.*` flag + the `CertificateAccessException` it raises. Would make V2 a one-command answer.
4. **`echo_publish` provider (seam marker)** — detect `Process.tell/ask(ProcessDns.*, new XxxMsg(...))` and `*.Async.On*(...)`. Can't cross the actor boundary but tags the publish site with the message type.
5. **`config_setting` read/write** — detect `Settings.Get<T>/Set<T>` (`[CallerMemberName]`-keyed). Traces config deps + flags the GI4327 hard-coded-key↔property-name coupling (N3).
6. **Cross-service communication detection (sync + async)** — classify and surface every inter-process / external comm edge as a first-class effect, split by delivery semantics. Grounded in the full inventory: [audits/2026-06-14/cross-service-comms-inventory.md](audits/2026-06-14/cross-service-comms-inventory.md). The goal: `rig` answers *"what other services/systems does this entry point talk to, and is it sync or async?"*
   - **Sync RPC (request/response)** — HTTP/REST (`HttpClient`/`WebClient`/`Flurl` — partly DONE), SOAP (`SoapHttpClientProtocol`), WCF, gRPC, FHIR (`Hl7.Fhir.Rest.FhirClient`). Tag with target host/endpoint where minable; carry the response type as the resource.
   - **Fire-and-forget (sync call, response discarded)** — outbound webhooks (`HttpClient.PostAsync` to customer URLs), Azure EventGrid publish, one-way HTTP notifies.
   - **Async (decoupled)** — message buses / brokers (none today — confirmed no MSMQ/RabbitMQ/ServiceBus/Kafka, so this arm is forward-looking), **Echo actors** (`Process.tell` = fire-and-forget, `Process.ask` = request/response — the existing `echo_publish` seam, classify under async), Redis pub/sub (`ISubscriber.Publish/Subscribe`), and the **ObjectStore DB-as-queue** handoff (`Chamber.ObjectStore` + a background poller).
   Pairs with the boundary-marker work (D4) so each cross-service edge is tagged rather than silently dropped. **Depends partly on F1** (the delegate-body gap currently hides several of these — e.g. the webhook `PostAsync`), so the tracer fix unblocks full coverage.

---

## D. Tool capability / UX (P3 — friction hit this session; reviewer's top-2 = D1, D2)

1. **`rig diff <ref>` / branch-aware indexing** — biggest workflow gap: had to reconcile index timestamp vs HEAD commit timezone to trust the index matched the MR branch. Want: map changed methods→runs, warn "index SHA ≠ working SHA, re-mine", and `rig reaches --changed` (effects for only diff-touched methods = the V7 task).
2. **`rig impact <method>`** — fuse forward+reverse: return `{entry points reaching it, effects it triggers, shared resources written}` in one shot (the actual reviewer question; today = `reaches` + `callers --roots` + `path` stitched mentally).
3. **Edge confidence/provenance flags** — annotate edges `resolved | dispatch-fanout(N) | error-type-recovered | unresolved-overload`. (The `Save()` over-count that motivated this is fixed, but explicit tagging still guards against silent mis-trust.) The `!:` recovery already exists internally — surface it.
4. **Boundary markers in `tree`/`reaches`** — when a trace dead-ends at `Process.tell` / `[ClientAction]` / `Activator.CreateInstance` / an interface with no in-scope impl, print `⊘ boundary: echo .tell (effects beyond invisible)` instead of silently stopping. Half the "rig cannot adjudicate" list is exactly these seams.
5. **`--format json` everywhere + stable DocIDs** — for `reaches`/`callers`/`path`, so an agent/CI gate consumes results without text-scraping.
6. **`rig assert` / policy gate** — codify a claim as a check, e.g. `rig assert no-path "PageService.DoRequest" "object_store write"`. Turns a one-off audit into a regression guard (natural home for "the owner-chamber guard must sit on every `Set*` path").
7. **Effect grouping / dedup in `reaches`** — collapse sibling-override walls (`AbsenceReasonEntity.Save`, `AppointmentTypeEntity.Save`, …) to `llblgen write ×N via EntityBase.Save dispatch [expand]` (rollup-by-cause).
8. **Quote-the-source mode (`--source`)** — inline the 1–2 relevant source lines per hop in `path`/`reaches` (cut ~8 tool→Read round-trips this session).
9. **Index health as an exit code** — `rig runs --check` returns non-zero when any in-scope run shows the base-type-chain flake (EP≈0/effects≈0 with healthy symbols), so a pre-audit script catches a bad mine.

---

## Suggested first slice
- **DONE**: the four 2026-06-14 effect-rule fixes (Flurl/WebClient/XmlDocument/UpdateMulti).
- **Next (cheap, high recall)**: F2 + F4 — EP-detector rules for SignalR / ApiController / `[WebMethod]` / `Page_Load` / servlet, and loosen the Echo-inbox name + `IService` base conventions.
- **Then**: F1 (delegate-body tracing) — deepest and highest-impact; it also unblocks C6 (cross-service comm detection).
- **And**: D1 (diff/branch awareness) — prevents silently auditing the wrong code.
