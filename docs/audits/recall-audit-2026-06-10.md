# Recall audit — 10 random entry points + spotting unnoticed entry points (2026-06-10)

Index: single-workspace `rig index --from MedDBase.csproj --rules …` (138k symbols / 1.08M refs / DI 145).
Method: 10 random entry points; for each, effects traced from SOURCE (ground truth) vs `rig reaches`/`tree`;
flag what rig MISSED. Flag-only — no fixes applied.

## Recurring effect-recall gaps (ranked by frequency across the 10)

1. **Generated clientpage `*Proxy` classes are not indexed** (5/10: ShowPatientRecord, BookService,
   ManageDialog.EditRow, OnDataTableSelect, + AdminNotesAlert). `RequestResponseProxyGenerator` emits
   `<Page>Proxy : ProxyBase` at build time; those types aren't in the index, so `clientpage_proxy
   show/redirect/ShowDialog` effects are invisible — the *primary* navigation effect of most UI actions.
   FIX: index the generated proxy assembly, OR add a fact rule matching `new *Proxy(page).Show/
   ShowDialog/Redirect` by **receiver-type-name-ends-with "Proxy"** (no resolved type needed).
2. **LINQ-to-LLBLGen reads via `new LinqMetaData().Table.Where(...)`** (4/10: ContactLog, BillForItems,
   HandleRuleSaved, CheckoutService, SAR). The llblgen detector matches only `new XxxEntity(pk[,txn])`
   ctor-fetch + `.Save()` + typed-list `Fill/GetMulti`; it misses every LINQ `IQueryable` query — often
   the *main* data read on the path. FIX: detector for `LinqMetaData.<Table>` property-accessor queries.
3. **Static `DFS.*` blob store** (CheckoutService, SAR): `DFS.Save/Load/Delete/Exists` (cloud blob:
   Azure/GCS/S3) — uncovered (rules only know `Core.IObjectStore`), AND the `dfs` project is `projects.
   exclude`d. FIX: `object_store` rules for the static `DFS` API (+ consider un-excluding dfs).
4. **`Echo.Process.tell/.ask`** (CheckoutService, SAR, …): direct static actor sends are uncovered
   (rules only know `ChamberMsg.tell`); also the actor boundary. FIX: a `chamber_msg`/`actor_msg`
   publish-site marker on `Echo.Process.tell/ask` (D4 boundary markers) — tag the send, don't cross it.
5. **Outbound HTTP / queue client APIs** (Webhooks, SAR, ContactLog): `HttpClient.PostAsync/
   GetByteArrayAsync`, `WebClient.UploadData`, Flurl, `EventGridPublisherClient.SendEvent`,
   GCP `PublisherClient.PublishAsync`, Redis `IRedisConnection.Enqueue/PublishToChannel`. No http/queue
   detectors for the real client surfaces. FIX: http/queue provider rules keyed on these APIs.
6. **Raw `ClientResponse.AddAction(new ClientAction("Redirect"))`** (OnDataTableSelect): the low-level
   nav bypass of the typed `ActionsHelper.RedirectUrl` path the `clientpage_nav` rule covers. FIX: rule
   on `AddAction` with a `ClientAction("Redirect")` literal arg.
7. **DI-provided interface dispatch not resolved** (HandleRuleSaved, BillForItems):
   `IBillingDataService.GetRules/GetRule` (resolved via `Chamber.ProvideService<T>()`) isn't bound to
   `BillingDataServiceCore`, so the reads behind it are missed. Recall/binding gap (DI-by-T, not ctor).
8. **Lazy property-accessor writes** (SAR `Certificate` getter does `.Save()` on first access):
   fundamental — property access isn't an invocation fact.

The session's prior fixes were re-validated here: dispatch fan-out is now rolled up (V1), object_store
read fires (V6/B1), ctor-fetch+Save in worker methods is captured (A2), and the AssertRight continuation
path resolves (A3). The gaps above are NEW detector/indexing opportunities, not regressions.

## How to spot UNNOTICED entry points (methods invoked externally that no EP rule recognizes)

An unnoticed entry point is invoked by the framework / reflection / DI / a registry / an external
process, but matches no EP rule — so rig never treats it as a root. Signals, by precision:

1. **Attribute census (highest precision).** An EP is usually *marked*: `[ClientAction]`,
   `[OperationContract]`, `[WebMethod]`, `[HttpGet/Post/Route]`, `[Fact]/[Test]`, RPC/job attributes,
   event-subscription attrs. Enumerate attribute-usage refs on methods; for each distinct attribute,
   check whether an EP rule covers it; **uncovered dispatch-attributes → their decorated methods are
   unnoticed EPs.** Catches EPs even when also called internally (so they aren't "dead").
2. **Method-group handoff targets with no direct caller.** rig derives delegate/method-group handoffs
   (e.g. `RepeatingBackgroundProcessSchedule(.., Process)`). A handoff target that's otherwise unreached
   is an EP reached via registration. Intersect `handoff-targets ∩ dead`.
3. **Override of / implementation from a METADATA (out-of-scope) base or interface.** If a first-party
   method overrides a virtual or implements an interface member whose declaring type lives in a
   *referenced assembly* (not first-party source), the framework calls it but rig sees no caller → a
   framework callback. (`rig dead --include-dispatch` surfaces these; refine by "base in metadata".)
4. **0-caller dead methods ranked by forward reach / effect count (the structural tell).** Genuinely
   dead code is a leaf or small cluster. An unnoticed EP **roots a large, live-looking subtree** (calls
   real services, hits effects) yet has **0 callers**. So `dead ∩ {0 callers} ∩ {high `rig reaches`
   count / hits effects}` = strong candidates. Rank by subtree size.
5. **Naming/signature conventions** (`On*`/`Handle*`/`Execute`/`Run`/`Startup`/`Process`, signatures
   `(object,EventArgs)`, `(TMessage)`, `Task RunAsync(...)`). Weak alone; combine with #4.

**Necessary-but-not-sufficient:** `rig dead` only catches EPs reached *exclusively* externally. An EP
also called internally won't be dead — so pair the dead-code scan (#2–#4) with the attribute/interface
census (#1, #3), which catches both.

**The closing feedback loop:** each confirmed candidate → add/extend an EP rule (its attribute → an
action rule; its base type → `classInheritance.baseTypes`; its registrar → a handoff rule) →
`rig derive` → the method *leaves the dead list AND* its effect subtree becomes attributed to a real
entry point. Re-run; the dead list converges to genuinely-dead + still-unnoticed. Iterate.

**Scoping caveat (observed):** on a mixed index (app + vendored Echo.Process / FirstDataBank drug libs),
the raw `rig dead` list is dominated by *library internals* whose true entry points are tests/external
hosts out of scope. Scope the EP-hunt to first-party **app** namespaces, or the noise buries the signal.

### Suggested tool support (not built)
- `rig entrypoints --candidates` (or `rig dead --as-entrypoints`): the dead set filtered+ranked by the
  signals above (attribute-marked, handoff target, metadata-override, 0-caller-high-reach), scoped to
  given namespaces — the operational form of this method.
- `rig attributes` census: distinct method attributes × covered-by-an-EP-rule? — surfaces #1 directly.
