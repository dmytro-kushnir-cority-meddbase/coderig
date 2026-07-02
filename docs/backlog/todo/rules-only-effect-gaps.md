## Rules-only effect / detector gaps (no engine change)

**Status:** todo — rules-only wins; no re-mine needed (derived at query time)
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

### VS-G8 — BCL filesystem types partially unmodeled (follow-up)

The 2026-06-15 quick-win batch added `FileStream`, `FileInfo`, `TextReader`/`TextWriter`. Any residual BCL
members not yet covered: audit `builtin-rules.json` against the evidence set below before claiming done.
Evidence: `dfs/CheckSum.cs:16`, `SharpMessage.cs:795`, `ServerFileService.cs:57`.
Status: partially done; verify coverage is complete.

### VS-G9 — BCL `Microsoft.Extensions.Caching.Memory.CacheExtensions.Get/Set/GetOrCreate<T>` residual

The 2026-06-15 batch anchored `inproc_cache:read/write` on the first-party `MemoryCacheWithInvalidation`
wrappers. Any call sites that use `CacheExtensions` directly (not via the wrappers) are still dark.
`MemoryCacheWithInvalidation.cs:36-79`. Low priority — the wrappers cover the dominant usage.

### VS-G14 — LanguageExt `HashMap` used as process cache not modeled

`PersonCache.GetPerson` (`Find`/`AddOrUpdate`) on `LanguageExt.HashMap` looks like a pure DB read when it is
in fact a process-level cache. `Pathways.IO/Accounts/PersonCache.cs:21-30`. Fix: `inproc_cache:read/write`
rules on `HashMap.Find`/`AddOrUpdate` (or accept as out-of-scope).

### F4 — `*Inbox` suffix-match needs rule-schema support

`InstanceInbox` exact-name is in the meddbase `rig.rules.json` `echo-inbox names` list. A `*Inbox` suffix
match (glob/pattern in rule schema) would lift the brittle exact-name list to a convention. Currently the
rule schema only supports exact names — rule-schema enhancement needed. Low.

### VS-G12 — `TextReader.ReadLine`/`ReadToEnd` base-class upcast (DONE — verify)

Added to the 2026-06-15 batch via the `TextReader`/`TextWriter` additions to `builtin-rules.json`. Verify
that the upcast case (`StreamReader` → `TextReader`) is covered. Evidence: `lab/Labs.Common/Logic/TDL.cs:75-84`.
