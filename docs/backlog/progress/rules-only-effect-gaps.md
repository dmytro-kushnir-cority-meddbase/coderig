## Rules-only effect / detector gaps (no engine change)

**Status (2026-07-06): only F4 remains open** — everything else below is resolved (shipped, verified
correct as-is, parked as a documented known-FP, or attempted-and-reverted on real-store calibration).
Moved todo → progress accordingly.
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (VS-G8, VS-G9 partial, VS-G6 partial,
VS-C2, VS-C4, VS-G14, F4 suffix-match, plus VS-C1/VS-G10/VS-G11 rule-layer follow-ons)

All items here are **rules-only** (add/edit entries in `builtin-rules.json` or `rig.rules.json`).
No extractor changes, no re-mine required unless noted.

---

### VS-C2 — Dapper `ExecuteScalar*`/`ExecuteReader*` classified as write (`dapper:execute`)

Reads classified as writes. `AuditsRepository.cs:24`: `ExecuteScalarAsync<long>` (a COUNT) shows
`dapper:execute`. Independently confirmed. `ExecuteScalar*`/`ExecuteReader*` should be `dapper:query` (read).

**Exception** noted in the register: `INSERT … SELECT SCOPE_IDENTITY()` is a scalar write — so
`ExecuteScalar` is genuinely ambiguous without SQL parsing. The register's resolution: stays `execute`
(can't classify without SQL parsing). **Leaving here as a documented known-FP**, not an actionable rule
change. If revisited: a separate `dapper:execute_scalar` op that reads can be a fresh rule entry rather than
reclassifying all `ExecuteScalar*`.

### VS-C4 — `XmlDocument.Save(path)` resource labelled `Xml.XmlDocument` (the receiver) not the file — ✅ SHIPPED (2026-07-02)

`io:write` fires but names the wrong resource; the correct resource is the file path argument.
**Shipped as a NEW strategy, not the card's plain `string_argument`** — that would have been a recall
regression: `ResolveResource` drops the effect on a null resource, so every `Save(pathVariable)` and the
`Save(Stream)`/`Save(XmlWriter)` overloads would have LOST their `io:write`. Instead:
`string_argument_or_receiver` (`FactEffectDeriver.ResolveResource`) = argument's string template, else
receiver, else declaring type — never drops (mirrors `http_argument`'s recall stance); honors
`argumentIndex`. Both `xml_document_save_file` AND the symmetric `xml_document_load_file` rules flipped.
Tests: `StringArgumentOrReceiverEffectTests` (new file). MedDBase A/B: identical counts (968 read /
9 write XmlDocument + 4 XDocument) — zero recall loss, and zero re-resourced sites because **no XML
Save/Load call site on MedDBase has a literal path arg** (the VS-C4 site itself,
`Master_HealthcodeServiceImpl.cs:1030`, computes the path via `ExportQueue.BuildUniqueMessageFilename(…)`
— a call expression, which mines no template and no `argument_name` either). The improvement manifests
only on literal/interpolated-path call sites, pinned by the unit tests.

### VS-G8 — BCL filesystem types partially unmodeled — ✅ AUDITED, no gap (2026-07-06)

Traced all three evidence sites to their actual BCL calls: `dfs/src/CheckSum.cs` (`FileStream.Read`),
`SharpMessage.cs` (`FileInfo.OpenWrite`/`FileStream.Flush`/`FileInfo.Delete`), `ServerFileService.cs:57`
(`HttpContent.CopyToAsync` — the `FileStream` is an ARGUMENT there, not the receiver; adding `HttpContent`
to an `io:write` rule would misclassify every stream-copy, not just file writes, so not fixable as a
`declaringTypes`-only edit). All three are already covered by the 2026-06-15 batch. Residual gaps noticed
in passing (property setters `FileInfo.LastWriteTime`/`CreationTime`, `Path.GetTempFileName()` silently
creating a file) are real but need a NEW method/declaringType or property-based matching (no precedent in
`builtin-rules.json` for `get_`/`set_` rules) — a separate future item if ever prioritized, not a gap in
this audit's scope.

### VS-G9 — BCL `Microsoft.Extensions.Caching.Memory.CacheExtensions.Get/Set/GetOrCreate<T>` — ✅ SHIPPED (2026-07-06)

Found a real bug while fixing this: the existing `inproc_cache:write` rule's `declaringTypes` referenced
`"Microsoft.Extensions.Caching.Memory.MemoryCacheExtensions"` — a type that **does not exist** anywhere in
the package (checked 1.0.0 through 10.0.9 via decompile); the real type is `CacheExtensions`. So the rule
never matched any real call site — dead since it was written. Fixed the typo and added the same type to the
`read` rule. MedDBase: 7 real hits (`Microsoft.Extensions.Caching.Memory.CacheExtensions` direct calls),
now correctly classified. Tests: `RulesOnlyEffectGapsTests` (new file).

### VS-G14 — LanguageExt `HashMap` used as process cache — ❌ ATTEMPTED, REVERTED (2026-07-06)

Built `inproc_cache:read/write` rules on `HashMap.Find`/`AddOrUpdate` per the `PersonCache.GetPerson`
evidence, then calibrated against the real MedDBase store per the hazards-doc discipline: **350 hits**,
dominated by ordinary functional-collection usage with no relation to caching (`QueryGenerator.GetConditions`,
`Schema.FindTablePrimaryKeyDataType`, `EchoClusterConfProvider.Load`, …) — `LanguageExt.HashMap` is a
general-purpose immutable collection used throughout the codebase's functional style, not cache-specific.
There is no fact-based way to distinguish "this HashMap instance is a cache cell" (e.g. wrapped in an
`Atom<HashMap<K,V>>` static field) from "this HashMap is a plain local/parameter" without flow analysis —
out of scope for this tool. Reverted; accepting VS-G14 as permanently out-of-scope, same disposition as
VS-C2.

### F4 — `*Inbox` suffix-match needs rule-schema support (still open)

`InstanceInbox` exact-name is in the meddbase `rig.rules.json` `echo-inbox names` list. A `*Inbox` suffix
match (glob/pattern in rule schema) would lift the brittle exact-name list to a convention. Currently the
rule schema only supports exact names — rule-schema enhancement needed (this is an actual engine/schema
change, not rules-only, despite this doc's title — flagging that mismatch explicitly). Low priority.

### VS-G12 — `TextReader.ReadLine`/`ReadToEnd` base-class upcast — ✅ VERIFIED (2026-07-06)

Confirmed via `lab/Labs.Common/Logic/TDL.cs:75-84`: a `StreamReader` held in a `TextReader`-typed variable
resolves its `.ReadLine()` call to `M:System.IO.TextReader.ReadLine` (Roslyn's declaring-type resolution
for an inherited-but-not-overridden method), which the existing `stream_read` rule's `declaringTypes`
already includes. Fires correctly; no change needed.
