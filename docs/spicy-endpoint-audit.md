# Spicy-endpoint effect/trace audit (2026-06-16)

Full `rig tree --raw --full` + `reaches`/`path` audit of four integration-heavy MedDBase entry points, each chosen for a different subsystem. Goal: find unmapped effects, dispatch over-approximations, and notable reach. Verified against source in `c:/git/meddbase-main-application`.

Endpoints:
- `AI/SmartLetter.SendMessage` — OpenAI/LLM
- `InvoiceMain.AddBillingItem` — Xero / billing
- `PatientPortalHttpHandler.ProcessRequestAsync` — patient-portal HTTP handler / object store
- `DetailsLive.DownloadMedicalRecord` — Subject Access Request (GDPR export)

## Cross-cutting findings (prioritised)

### 1. [rig bug — HIGH] `path`/`callers` over-traverse vs `reaches`/`tree`
`path` and `callers --entrypoints` report reaches that `reaches`/`tree` correctly do **not**. Surfaced on `InvoiceMain.AddBillingItem`, which `path`/`callers` claim reaches the Xero accounting API (161 false "entry points" to `Xero2.BeforeDebtorInvoiceSent`); the real Xero trigger is the invoice-*Send* action, not adding a billing item.

**Root cause (confirmed):** only `ReachesCommand` and `TreeCommand` call `FactPathFinder.MarkEventSubscriptionHandoffs` (which reclassifies event-subscription `+=` method-group edges to `handoff` → sync-cut). `PathCommand` and `CallersCommand` do **not**, so they walk:
- event-subscription method-group edges (`proxy.Changed += new BillingItemListChanged(Handler)`, `InvoiceMain.cs:148`) as if synchronous calls, and
- `nameof(Method)` references embedded in static `DomMap` menu fields (`ActionItem("Make Payment", nameof(MarkSentMakePayment))`, `InvoiceMain.cs:668-674`) as call edges.

On this menu-driven Pages codebase (97k call edges, `nameof` action maps + `+=` wiring everywhere) this produces large false-positive reach sets. **Fix:** apply `MarkEventSubscriptionHandoffs` in `PathCommand`/`CallersCommand` too (and/or sync-cut `nameof`-in-field reference edges), aligning their edge selection with the effect engine. Files: `Rig.Cli/Commands/PathCommand.cs`, `CallersCommand.cs`; edge extraction `Rig.Analysis/Extraction/FactExtractor.cs`.

### 2. [rule gap — HIGH] `config` under-match: the `IConfiguration`/`IPersistentState.GetItem` family
The project `config` rule matches `GetItem`/`ContainsKey` only on `MMS.ConfigurationManager`/`IAppSettings`. But the app reads config (including **secrets** — the OpenAI **API key**, `Settings.OpenAiKey`) via `Settings.Get<T>` → `IPersistentState.GetItem<T>` → `AccountConfiguration`/`WebConfiguration`/`PersistentApplicationConfiguration.GetItem<T>`, which read `configuration[key]` (in-memory dict) or `ConfigurationManager.AppSettings[key]` directly — **not** always through the low-level `MMS.ConfigurationManager.GetItem` anchor. Result: `reaches` shows **0 `config` effects** for SmartLetter despite it reading 6 settings incl. the API key.

The earlier "anchor low + let reachability fan it out" decision has a hole: reads that resolve to `configuration[key]`/web.config never hit the low-level anchor. **Fix:** add a second `config:read` matcher on `GetItem`/`ContainsKey` for receiverTypes `MMS.CommonInterfaces.IPersistentState`/`IConfiguration`/`IPersistentApplicationConfiguration` + the concrete config impls. Caveat: pervasive — high recall; and `IPersistentState.GetItem` also serves `IPersistentApplicationState` (state, not config), so prefer the concrete `IConfiguration` impls or the `IConfiguration` receiver to avoid mis-tagging state reads. (Pairs with the interface-receiver narrowing already shipped.)

### 3. [rule gap / new provider — HIGH] Inbound HTTP response IO is untagged
The `io` provider is file-only (`System.IO.*`); `http` is outbound-only (HttpClient/WebClient/OpenAI); the `http` rules that match `HttpTaskAsyncHandler` are **entry-point** detectors, not effects. So an ASP.NET handler's entire response surface produces **zero** effects — including a **document-download byte stream to the client**:
- `HttpResponse.OutputStream.Write` (`PatientPortalHttpHandler.cs:143,156`), `Response.Write` (`:183`), `Response.AppendHeader`/`AddHeader`, `Response.Cookies.Add/Remove`; request reads `Request.QueryString`/`Headers`/`HttpMethod`.

**Fix:** new provider (e.g. `http:response_write`/`http:stream`) on `System.Web.HttpResponse`/`HttpRequest`. Affects every `IHttpHandler`/`HttpTaskAsyncHandler`, not just this one.

### 4. [rule gap — LOW] `object_store` `DeleteIfExists` missing from the DFS rule
The DFS→`object_store` rule covers `Save`/`Load`/`Exists`/`Delete` but not `DeleteIfExists` (`SubjectAccessRequestProcessingService.cs:141`). Not reachable from the audited endpoints (it's in `DeleteSubjectAccessRequest`), so low priority — add when convenient.

### 5. [confirmation — GOOD] The dispatch-precision fixes hold
Across all four traces, the recently-shipped fixes (one-hop dispatch, cross-kind closure, interface-receiver narrowing) showed **no regressions**. Every `«impl-dispatch ×N fan-out»` scrutinised was either a correct CHA over-approximation (disclosed in the `reaches` "dispatch fan-out (NOT a real call)" bucket) or a correct single-impl resolution (the `×N` is an edge-count label, one concrete child expanded). No impl→inherited-base→sibling-override leakage of the kind fixed earlier. Notable correct resolutions: `APIMessageBase.PostProcess ×2` (exactly 2 real overrides), `ExternalApplicationBase.BeforeDebtorInvoiceSent ×4` (the 4 real accounting providers), SAR `×7` labels (single-impl interface).

## Per-endpoint summary

| Endpoint | Verdict | Headline |
|---|---|---|
| `SmartLetter.SendMessage` | clean + 1 gap | config/API-key reads untagged (#2); PII→OpenAI with no explicit action gate (security note) |
| `InvoiceMain.AddBillingItem` | dispatch bug | `path`/`callers` falsely reach Xero (#1); effects clean; DB write per-service in a loop, permission-gated |
| `PatientPortalHttpHandler.ProcessRequestAsync` | 1 gap + security notes | inbound HTTP response IO untagged (#3); CORS-only fire-and-forget validation; token-gated anonymous object-store reach (security notes) |
| `DownloadMedicalRecord` | clean | properly gated (action + service + SQL, chamber-scoped); heavy SAR generation correctly NOT reachable (Rx-decoupled) |

## Security observations (MedDBase — for human / security review, not rig issues)

- **SmartLetter → OpenAI sends patient PII.** When `SmartLetterContext` is set, the context template (`Settings.cs:4885-4895`) embeds patient name + home address + clinician/site into the LLM `instructions`. `SendMessage` itself has **no** `AssertAccountRight` — the permission gate lives in `OnFirstInitialise` (page lifecycle), so confirm that gate always runs server-side before this AJAX action. Prompt-only calls (no context) reach OpenAI with no permission assert in their own graph.
- **PatientPortalHttpHandler `ValidateAccessControl` is CORS-only and fire-and-forget.** `:22-23` writes an error on CORS failure but does **not** `return` — execution continues into `Process`. Real auth is per-message-type (`if (!IsLoggedIn) throw NotLoggedInException` in `ProcessChartsRequest`/`ProcessDocumentsRequest`). The destructive `DeleteDocument` path is gated behind login **except** the intentional `DownloadMsg` (`RequiresLogin => false`, CSRF-skipped) which reaches object-store read + conditional delete pre-auth, protected only by an unguessable document token. Confirm that's acceptable.
- **Dead flag:** `RequiresLogin` is overridden on ~50 message types but **never read** anywhere — auth relies entirely on the per-handler `IsLoggedIn` checks. A new message author may wrongly assume `RequiresLogin=true` gates them. Code-hygiene risk.

## Recommended actions (rig)

1. Align `path`/`callers` edge selection with `reaches`/`tree` (apply `MarkEventSubscriptionHandoffs`; sync-cut `nameof`-in-field edges). **(#1 — biggest correctness win)**
2. Extend `config:read` to the `IConfiguration` impl family (so config/secret reads surface). **(#2)**
3. Add an inbound `System.Web.HttpResponse`/`HttpRequest` effect provider. **(#3)**
4. Add `DeleteIfExists` to the DFS `object_store` rule. **(#4, minor)**
