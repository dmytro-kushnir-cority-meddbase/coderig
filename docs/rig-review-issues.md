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
- **F2 — EP-detector rules for missing kinds.** builtin `entrypoints.classInheritance`: Web API `ApiController`/MVC `Controller` → http, ASMX `WebService`+`[WebMethod]` → soap, WebForms `Page` lifecycle → page, SignalR `Hub` → signalr. meddbase `rig.rules.json`: bespoke `MedDBase.AppCode.HubBase` → signalr, DataServer `ServletBase`/`IServlet` → http. Verified: `DownloadsController`/`FileController`/`FieldService`/`IndividualSchedule.Page_Load`/`ServletBase` now in `--entrypoints`; LegacyNet48 golden set updated (+7 Web API actions).
- **F4 — EP convention loosening.** meddbase `rig.rules.json`: `IService` added to the `servicebase.startup` base types; `InstanceInbox` added to the Echo-inbox names. Verified: `AppStartupProcesses.Startup`, `PathwayInstance.InstanceInbox` now in `--entrypoints`. (Exact-name inbox matching is still brittle — a `*Inbox` suffix match needs rule-schema support; deferred.)
- **Library I/O effect detectors (16+ rules).** Triaged every unique third-party lib in the solution for I/O surface; added meddbase `rig.rules.json` rules for twilio, sendgrid, gcp_pubsub, ldap, ironpdf, script_eval, linq2db, EventGrid + (after ground-truthing the real SDK API) openai, cefsharp, xero. Full triage + the three mis-typed-rule corrections: [audits/2026-06-14/library-io-detector-triage.md](audits/2026-06-14/library-io-detector-triage.md). **Net firing in the store: 11 detectors.** Dapper/Pgp/Parquet/LibGit2Sharp are in separate unmined solutions (`audits.slnx`/`ClientDataTransformation.slnx`/`sql-runner.slnx`); RestSharp is unused repo-wide.

### Open findings — triage

| ID | Finding | Root cause | Sev | Effort | Found on |
|----|---------|-----------|-----|--------|----------|
| ~~F1~~ | ~~Delegate/lambda **body** not traced through a wrapper/monad → effect invisible~~ | **REFUTED 2026-06-14** — lambda + query-clause bodies ARE traced; the flagship examples failed for two *other* reasons (F1a, F1b below). See investigation note. | — | — | — |
| ~~F1a~~ | ~~`http_argument` DROPS the effect when the URL is a variable → codebase-wide HTTP blind spot~~ | **DONE 2026-06-14** — `ResolveResource` falls back to receiver/declaring type instead of dropping; verified live (`WebhookHttpClient.Send`, `ApiClient.GetAccessTokenAsync` now report `http POST`; ~POST 29 / GET 23 sites derived) | — | — | webhooks `httpClient.PostAsync`; NHS `ApiClient.GetAccessTokenAsync` |
| ~~F1b~~ | ~~Invocations Roslyn resolves only to a **CandidateSymbol** (net48 `!:` partial binding) silently dropped~~ | **DONE 2026-06-14** — ref walk falls back to the first `CandidateSymbol`; regression test verified red-without-fix. Needs a re-mine to surface in the existing store | — | — | PACS `FileExt.Move/Delete/CreateMissingFilePathFolders` |
| ~~F2~~ | ~~`--entrypoints` misses non-`[ClientAction]` EP kinds~~ | **DONE 2026-06-14** — rules added (builtin + meddbase), verified | — | — | SignalR `HubBase`; Web API `ApiController`; ASMX `[WebMethod]`; `Page_Load`; DataServer `ServletBase` |
| **F3** | Background detector tags the **wiring** method, not the scheduled **delegate target** | `BackgroundProcessSchedule(…,Callback,…)` target not promoted to EP (Layer-2 handoff) | **Med** | Med | `CheckForZeroDebt`, `ProcessHealthcodeQueue`, `DoDueActions`, `ReferralSLAService.Worker` |
| ~~F4~~ | ~~Echo inbox name too tight; `IService` vs abstract `ServiceBase`~~ | **DONE 2026-06-14** — meddbase rules loosened, verified (suffix-match still deferred) | — | — | `PathwayInstance.InstanceInbox`; `AppStartupProcesses.Startup` |
| **F5** | Cross-project call edges dropped | scoped-mine stitch gap | **Med** | Med (miner) | `SubmitToHealthcode → ExportQueue.*` (Core.Background not stitched to Workflows) |
| **F6** | Silent stops at boundaries (external assembly, clientpage_proxy, cross-deployment Redis) | no seam tag emitted (= D4) | **Low-Med** | Low | `RtfPipe.Rtf.ToHtml`; Medicare dialog proxy; webhook Redis handoff |
| **F7** | `--roots` precision noise: `P:`/`F:` roots, interface stubs, unpropagated `save:false` guard | reverse-walk heuristic + no constant propagation | **Low** | Low | filterable by prefix |

**Positives confirmed**: the forward `Save()` fan-out fix (retired A1) holds AND reverse base-virtual dispatch is precise (no leak); interface dispatch resolves (`IWebhooks`→concrete); `--roots` recovers ~100% of the EPs `--entrypoints` misses.

**Remaining**: **F1a** + **F1b** DONE 2026-06-14 (F1b needs a re-mine to surface live). **F3** (background delegate-target promotion) and **F5** (cross-project stitch) are medium. F6/F7 are low. The cheap rule slice (F2 + F4 + the four effect rules) is DONE.

### F1 investigation (2026-06-14) — the original "delegate-body tracing" finding was a misdiagnosis

Drove the two flagship F1 examples to ground truth (store + source + synthetic fixtures):

- **Lambda/delegate bodies ARE traced.** `EnclosingSymbolId` walks `FirstAncestorOrSelf<MemberDeclarationSyntax>()`, so a lambda is *transparent* — its invocations attribute to the enclosing named method. Proven live: `Interpreter.MoveExternalFile` reaches `GetAbsoluteQueueFilePath`, which is called **only** inside a `Try(() => …)` lambda. Proven by construction: `FactExtractorCaptureTests.Captures_invocations_in_query_clause_expressions` — invocations in a query SOURCE expression, inside an explicit lambda, AND in a desugared `SelectMany` collection-selector are all captured (instance- and extension-method query operators both). So the premise "the effect inside is invisible" is false.
- **Webhook `httpClient.PostAsync` → it's F1a, not a lambda gap.** Even the *direct* (non-lambda) `ApiClient.GetAccessTokenAsync` PostAsync shows 0 effects. Cause: the builtin rule's `resource:"http_argument"` drops the effect because the URL is a variable.
- **PACS `FileExt.Move/Delete` → it's F1b.** `FileExt.*` are declared first-party symbols in the *same* mined project, but their invocations carry 0 refs while sibling `this`-calls in the same method resolve. Synthetic query-clause repro does NOT reproduce → the cause is mine-time symbol resolution: `GetSymbolInfo(name).Symbol` is null (Roslyn returns a candidate under net48 partial binding) and the ref walk discards it.

Regression tests added: `Captures_invocations_in_query_clause_expressions` (guards the refuted premise) and `Effect_rule_matching_is_exact_and_declaring_type_resource_survives_unminable_receiver` (codifies the Xero/OpenAI/CefSharp matching-semantics fixes).

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
10. **Rule-coverage / zero-match diagnostic** (`rig derive --rule-stats` or `rig rules --coverage`) — report a per-rule firing count so a rule that matches **nothing** is flagged instead of failing silently. This session, three effect rules (OpenAI, CefSharp, Xero) were written against *guessed* SDK surfaces and silently produced **0 effects**; each was only caught by hand via `rig refs`. Real causes are unintuitive: concrete-class vs interface (`AccountingApi` vs `IAccountingApiAsync`), wrong method names (`GetAccounts` vs `GetAccountsAsyncWithHttpInfo` — matching is *exact*), wrong API family (`Chat.ChatClient` vs `Responses.OpenAIResponseClient`). A zero-match flag would have surfaced all three instantly. Distinguish "rule matched 0 sites" (likely mis-typed) from "type present but 0 invocations" (dead/unused, e.g. RestSharp).

---

## E. Rendering & CLI ergonomics (P3 — designed 2026-06-14)

### E1. `tree --full` = maximal-fidelity tree (E1a DONE, E1b pending)

**E1a — effects as provenance leaf nodes (DONE 2026-06-14, commit `tree --full: render effects…`).**
In `--full`, each effect is promoted from the inline `{provider:op resource}` tag to its own call-site
leaf node (`provider:op + resource + file:line`), source-ordered ahead of the call children. Verified
live: `AuditsRepository.SubmitEvent --full` shows `dapper:execute …:15` under the method and
`db_connection:open …:44` nested under `WithConnection`. Default/`--effects`/`--summary` keep the compact
inline tag. Renderer-only, no re-mine; unit-tested (`TreeRenderRulesTests`).

**E1b — unresolved library calls as dimmed leaves (PENDING).** Surface library invocations that resolved
to a real target but matched no effect rule (e.g. `LanguageExt…Map`) as dimmed leaves in `--full`.
Wrinkle: the bounded per-method invocation set can't be loaded with a naive `IN (treeMethodIds)` query —
a large `--full` tree (`SubmitToHealthcode` ≈ 3,753 methods) exceeds SQLite's parameter limit. Correct
shape mirrors effect handling: derive the unresolved set from the already-bounded `ReachInputs` on the
cold path, and cache it in the render sidecar (alongside `SeamEffects`/`Locations`) so warm/cache-hit
queries don't re-query. Gated to `--full` only, so default/compact paths are never affected.

Today `tree` renders only **first-party** method nodes; a library call enters the tree **one of two ways**:
matched by an effect rule → hoisted to an inline `{• provider:op resource}` tag on the *enclosing* method;
unmatched → dropped entirely (no node, no tag). Two consequences a user hit this session:

- The **producing call is invisible** — `dapper:execute` shows on `SubmitEvent` but the `ExecuteAsync`
  call that caused it (and its line) is buried in the tag. Ambiguous the moment a method has >1 effect.
- **Unresolved library calls vanish** — `LanguageExt…Map` (no effect rule) appears nowhere, in *any*
  mode. `--full` does NOT surface it: `--full` only toggles effect-*pruning* of first-party branches
  (`SubtreeHasEffect`), an axis orthogonal to first-party-vs-library.

**Decision:** make `--full` do what the word says — the maximal-fidelity tree:
- every reachable first-party method (current `--full`), **plus**
- **effects as provenance leaf nodes** — `⚡ provider:op  Target.Method  file:line` at the call site,
  source-ordered with siblings (replaces the inline tag in this mode only), **plus**
- **unresolved library calls** (resolved to a real target, no effect rule) rendered **dimmed** as leaves.

Default / `--effects` / `--summary` keep today's compact inline-tag rendering. All data needed (target
method, file:line, receiver) is already stored — **renderer-only, no re-mine**. This folds the two
floated flags (`--effects-as-nodes`, `--show-unresolved`) into `--full`; they do not ship separately.
Temporal/happens-before ordering is explicitly **out of scope** (the tree is structural/lexical, not a
trace — e.g. the `WithConnection(callback)` inversion renders `execute` above the `open` it runs after).

### E2. Flag-surface unification (PROPOSED — breaking parts need sign-off)

The per-command flag surface has drifted (full audit + table in commit message / session notes).
Incoherences: dead-alias pairs (`--maxdepth`≡`--depth`, `--signatures`≡`--sig`); diagnostics
(`--time`/`--no-cache`) stuck on `tree` only; `--format tsv` ad hoc on 3 of N commands; `--limit`
coverage arbitrary (absent from the flood-prone `reaches`/`tree`/`callers`); the first-party/library
axis named two ways (`refs --first-party` vs `dead --lib`); a `--root`(value, dead) / `--roots`(bool,
callers) collision; mode flags as unvalidated booleans (`tree --full|--summary|--effects`,
`callers --roots|--entrypoints`).

**Target — three tiers, a flag means the same thing everywhere:**
- **Tier 1 global:** `--rules`, `--format text|tsv` (default text), `--limit <n>` (default unbounded),
  `--time`, `--no-cache` — valid on every command where sensible.
- **Tier 2 traversal** (`path`/`reaches`/`tree`/`callers`): `--depth <n>` (canonical; `--maxdepth`
  deprecated alias), `--async` (default sync), `--raw`, `--only`/`--exclude` (extend to `path`/`callers`).
- **Tier 3 command-specific:** `tree` projection group (validated, mutually exclusive) default·`--full`·
  `--summary`·`--effects` + `--signatures` (drop `--sig`) + `--files`; `callers` selector group
  `--orphans`(rename `--roots`)·`--entrypoints`; `dead` `--lib`→`--include-lib`, `--root`→`--from`,
  keep `--include-dispatch`/`--all`; `index`/`mine` as today.

**Breaking — approved + DONE 2026-06-14:**
- (a) renames: `callers --roots`→`--orphans` (deprecated `--roots` alias kept), `dead --lib`→`--include-lib`
  (deprecated `--lib` alias kept). NB: `dead --root` was **left as-is** — the `--orphans` rename already
  removes the `--root`/`--roots` collision, and reusing `--from` (which means an entry `.csproj` in
  `index`/`mine`) would have introduced a worse cross-command inconsistency.
- (b) `--maxdepth`/`--sig` deprecated: dropped from help, still accepted as aliases (one release).
- (c) `index` test default **flipped** — tests EXCLUDED by default; `--include-tests` opts back in,
  `--no-tests` accepted as a redundant no-op alias.
- Mode-group validation: `tree --full|--summary|--effects` and `callers --orphans|--entrypoints` reject
  conflicting combinations up front (before store access). Tests in `CliApplicationTests`.

**Deferred (Tier-1 generalization — real per-command plumbing, not just whitelisting):** promoting
`--time`/`--no-cache` (tree-only today; other query commands have no cache/timer to toggle), `--format
text|tsv` (needs TSV emitters for `tree`/`path`/`callers`), and `--limit` (needs truncation on
`reaches`/`tree`/`callers`) to all commands; and extending `--only`/`--exclude` to `path`/`callers`.
These are additive and can land incrementally.

### E3. Store-read guard (DONE 2026-06-14)

A query command run with no `.rig` store (wrong directory) or against a store built by an older rig
(schema drift, e.g. a column added since) previously threw an unhandled `SqliteException` stack trace.
Now `DispatchAsync` catches the BCL `DbException` and emits a clean exit-2 message: "No indexed store at
… — run `rig index` or cd to the owning directory" vs "built by an older rig (schema mismatch: …) —
re-index". Verified live on both. (This was the root cause of the "tree reports no effects" confusion —
the command had been run from the source repo against a stale store.)

---

## F. Validation sweep (2026-06-15) — 10 Sonnet agents, tree-vs-source

Two fan-outs of 5 agents each, all ground-truthed against MedDBase source (file:line cited):
(1) **tree-validation** over juicy endpoints — does what rig reports match the code?
(2) **false-negative hunt** over domains that yielded no effects/EPs — what does rig miss?
Below, **VS-Cn** = correctness defect (rig reports something WRONG), **VS-Gn** = coverage gap (rig
MISSES something real). Severity is the agents' ground-truthed assessment.

### Correctness defects (rig reports wrong / spurious)

| ID | Defect | Evidence | Sev | Fix locus |
|----|--------|----------|-----|-----------|
| **VS-C1** | **Rx `Observable.Subscribe` phantom path** — rig inlines the Subscribe lambda as a synchronous call, so any tree through `IPersistentState.GetItem`→`AccountConfiguration.InitialiseLogger` drags in spurious transitive effects (`http:POST` audit, `gcp_pubsub:publish`) that never fire synchronously. | `AccountConfiguration.cs:210-213` (Observable.Subscribe chain) → phantom `AuditLogService.SubmitViaHttp` http:POST | **High** | Add `IObservable.Subscribe` to opaque/boundary set (like Echo `tell`) — render+traversal |
| **VS-C2** | **Dapper `ExecuteScalar*`/`ExecuteReader*` classified as `execute` (write)** but they're READS. `AuditsRepository.Query` (a COUNT) shows spurious `dapper:execute`. *(Independently confirmed this session.)* | `AuditsRepository.cs:24` `ExecuteScalarAsync<long>` → `⚡ dapper:execute` | **Med** | `rig.rules.json` dapper rule — move ExecuteScalar*/ExecuteReader* to `query` (rules-only) |
| **VS-C3** | **`SemaphoreSlim.WaitAsync`/`.Release` misclassified as `lock:acquire`/`lock:release`** (Monitor). An async semaphore is not a monitor lock; pollutes `lock_held_across`/`resource_span` (anchors the span at the wrong line). | `MonitorQueueBackgroundService.cs:67` WaitAsync→lock:acquire; `:111` Release→lock:release | **Med** | lock detector — distinguish SemaphoreSlim (→ `semaphore:wait/signal`) from Monitor |
| **VS-C4** | **`XmlDocument.Save(path)` resource labelled `Xml.XmlDocument`** (the receiver) **instead of the file** — io:write fires but names the wrong resource. | `Master_HealthcodeServiceImpl.cs:1045` | Med | io rule — `XmlDocument.Save`/`XDocument.Save` resource → file/argument, not receiver_type |
| **VS-C5** | **Flurl `PutStringAsync` → `http:send`, not `http:PUT`** — POST/GET map correctly; PUT falls through to a generic bucket, so `http:PUT` filters miss it. | `MedicareApi/DeviceRegistration/AccessRequests.cs:15` | Med | Flurl verb map — add `PutStringAsync`/`PutJsonAsync` → PUT (+ DELETE/PATCH) |

### Coverage gaps (rig misses real effects / entry points)

| ID | Gap | Evidence | Sev | Root cause |
|----|-----|----------|-----|-----------|
| **VS-G1** | **Background work scheduled via constructor-arg delegate (`new BackgroundProcessSchedule(due, MethodGroup, name)`) is NOT promoted as an entry point** — only the `SetProcessDelegate` override form is. 10 dark background EPs incl. `ProcessHealthcodeQueue` (269 effects incl. **soap:submit**), `DoDueActions` (216), `CheckForZeroDebt` (92), `RaiseMembershipSchemeInvoices` (257). Evidences open finding **F3**. | `Master_HealthcodeServiceImpl.cs:239`, `PatientContact/Master.cs:165`, +8 | **Critical** | Detector rule: method-group passed as `BackgroundProcessScheduleDelegate` ctor arg → `background` EP (flow-insensitive; rules-only) |
| **VS-G2** | **`permission:assert` family entirely unmodeled** — `CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight`, `HasRight` (non-throwing), `PersonCache.IfCanView` (every patient load gates `CanViewPatientDemographic`). The `throw:raise CertificateAccessException` IS visible, but the Rights flag / gate semantic is not. 50+ entities. | `CertificateEntity.cs:977`, `PersonCache.cs:53`, `BillingItemEntity.cs:120` | **Critical** | New detector family (was C3) — emit `permission:assert <Rights.Flag>`; now evidenced |
| **VS-G3** | **`config:read` family entirely unmodeled** — `Settings.*` (CallerMemberName-keyed), `ConfigurationManager.AppSettings[key]`, `AccountConfiguration.GetItem`. Feature-flag branches that gate whether SOAP submission fires are invisible. | `Settings.cs:878`, `Master_HealthcodeServiceImpl.cs:1043/1048` (`WriteToFile`/`SendToService`) | **Critical** | New detector family (was C5); key is a compile-time literal/CallerMemberName |
| **VS-G4** | **SOAP non-Healthcode proxies unmodeled** — the soap rule is pinned to `declaringTypes:[HealthcodeWebServices]`; generic `SoapHttpClientProtocol.Invoke` isn't matched, so LabsServer (lab ordering), Mirth HL7, and Site callbacks are dark. | `LabsServer/Reference.cs:133`, `Mirth.SoapProxy.cs:87` | High | Rule with `declaringTypeBaseTypes:[SoapHttpClientProtocol]`, method `Invoke` → soap (evidences C6) |
| **VS-G5** | **FHIR (`Hl7.Fhir.Rest.FhirClient`/`BaseFhirClient`) entirely unmodeled** — NHS GPConnect + PDS outbound calls (`TypeOperationAsync`/`GetAsync`/`SearchAsync`) are dark. | `GPConnectService.cs:99`, `Nhs.Pds/Client/ApiClient.cs:25/32` | High | New `fhir` provider rule (evidences C6) |
| **VS-G6** | **`queue:read` unmodeled** — Redis `Enqueue`/`PublishToChannel` (write/publish) fire, but `GetAsyncQueue`/`SubscribeToChannel` (consume) have NO rule, so the whole Webhooks inbound pipeline looks like fire-and-forget HTTP. | `MonitorQueueBackgroundService.cs:76` (GetAsyncQueue), `:45` (SubscribeToChannel) | High | Add `queue:read` op to the Redis rule (asymmetric coverage) |
| **VS-G7** | **`object_store:read` missing for generic `GetInstance<T>`/`GetObjectInstances<T>`** — the primary ObjectStore read path. Non-generic reads (`GetIndexIdentifiers`) fire; the generic ones don't — a generic-arity (`` `1 ``) DocID-matching bug. | `ObjectStore.cs:622`, `:1095` | High | rig matcher — generic-method name match must ignore the `` `1 `` arity suffix while keeping declaring-type gate |
| **VS-G8** | **BCL filesystem types unmodeled** — `FileStream.#ctor(string,FileMode)`, `StreamReader.#ctor(string)`, `FileInfo.Delete/OpenWrite/MoveTo`, `FileStream.Read/Write`. Facts ARE indexed; no rule. `File.*` static methods fire fine. | `dfs/CheckSum.cs:16`, `SharpMessage.cs:795`, `ServerFileService.cs:57` | High | io rule — add these BCL members (rules-only) |
| **VS-G9** | **BCL `Microsoft.Extensions.Caching.Memory.CacheExtensions.Get/Set/GetOrCreate<T>` unmodeled** — `MemoryCacheWithInvalidation` wrappers delegate to these external extension methods → no `inproc_cache:read/write`. Metafield/JobTitle/CustomisedCredit caches look read-only or absent. | `MemoryCacheWithInvalidation.cs:36-79` | High | Anchor inproc_cache effect on the first-party wrapper methods (BCL ext is unresolved) |
| **VS-G10** | **Higher-order delegate-variable invocation drops effects** — effects attribute to the lambda DEFINITION site, not where the stored `Func<>` is invoked. `rig reaches Xero2Client.GetResult` (the sole runtime Xero dispatch) shows NO xero effects. Also `SubscriberManager.GetEndpoint` (memoized Func field) body dropped. | `Xero2Client.cs:95` `request(auth)`; `SubscriberManager.cs:27` | Med | Structural — Func-field/delegate-variable call resolution (hard; document the limit, prefer querying concrete methods) |
| **VS-G11** | **LanguageExt functional wrappers (`Try`/`map`/`bind`) hide inner I/O** — body inside `Prelude.Try(() => …)` is a delegate handoff; `new StreamWriter(path)` + writes inside go undetected. | `LogBoxUi.cs:45` | Med | Add LanguageExt combinators to `handoffDispatchers`, or treat synchronous ones (`Try`) as inlined |
| **VS-G12** | **Base-class dispatch `TextReader.ReadLine`/`ReadToEnd` not matched** — only `StreamReader.*`. A `StreamReader` upcast to `TextReader` loses the io:read. | `lab/Labs.Common/Logic/TDL.cs:75-84` | Med | io rule — add `TextReader.*` read methods (covers the upcast) |
| **VS-G13** | **`DelegatingHandler.SendAsync` not traversed** — HTTP made inside a custom `LoggingHandler` is invisible to trees rooted where the handler is wired. | `Nhs.Pds/Client/LoggingHandler.cs:33` | Low | Known DelegatingHandler blind spot — document |
| **VS-G14** | **LanguageExt `HashMap` used as a process cache not modeled** — Pathways `PersonCache.GetPerson` (`Find`/`AddOrUpdate`) looks like a pure DB read. | `Pathways.IO/Accounts/PersonCache.cs:21-30` | Med | inproc_cache rule for HashMap Find/AddOrUpdate (or accept as out-of-scope) |

### Cross-cutting themes
- **Higher-order / reactive seams** (VS-C1, VS-G10, VS-G11, VS-G13, and F3/VS-G1): the single biggest correctness theme — rig either inlines a deferred callback as synchronous (false positives, VS-C1) or can't follow a stored delegate (false negatives, VS-G10/G11). A coherent "deferred-execution boundary" model (opaque for Rx/Func-fields, inlined for synchronous combinators, promoted-EP for schedulers) would address five findings.
- **Rule-gap vs resolution-gap:** most coverage gaps are pure **rules-only** wins (VS-G6/G8/G9 + VS-C2/C4/C5) — add/relabel rules, no engine change, no re-mine. VS-G7 is a real matcher bug (generic arity). VS-G2/G3/G4/G5 are new detector families/providers.
- **`rig --files`/leaf paths are shortened tails** (e.g. `src/Audits/…`) not solution-root-relative (real: `src/audits/src/Audits/…`), so they can't be opened directly — every agent had to glob by basename. Worth making paths root-relative or absolute (ties to D8 quote-source).

### Suggested order (by value ÷ effort)
1. **Rules-only quick wins (no re-mine):** VS-C2 (dapper scalar), VS-C5 (Flurl PUT), VS-G6 (queue:read), VS-G8 (BCL file I/O), VS-G4 (generic SOAP), VS-C4 (XmlDocument resource). A few lines of `rig.rules.json`/builtin-rules each; re-derive at query time.
2. **High-value detector families:** VS-G2 (permission:assert) + VS-G3 (config:read) — biggest audit value; VS-G1/F3 (background ctor-delegate EP).
3. **Engine fixes:** VS-G7 (generic-arity match — likely small + broadly beneficial), VS-C1 + VS-C3 (boundary/primitive classification), VS-G5 (FHIR provider).
4. **Structural/documented limits:** VS-G10/G11/G13 (deferred-execution model) — design first.

---

## Suggested first slice
- **DONE 2026-06-14**: the four effect-rule fixes (Flurl/WebClient/XmlDocument/UpdateMulti) + F2 (EP-detector rules) + F4 (convention loosening).
- **Next**: F1 (delegate-body tracing) — deepest and highest-impact; also unblocks C6 (cross-service comm detection).
- **Then**: F3 (background delegate-target promotion), F5 (cross-project stitch), D1 (diff/branch awareness).
