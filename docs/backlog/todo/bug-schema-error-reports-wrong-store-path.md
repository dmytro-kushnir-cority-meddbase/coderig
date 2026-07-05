# Bug: schema-mismatch error reports the wrong store path

**Found:** 2026-07-05 (during web `impact` bring-up), running `rig impact --base 72b89… --head 4061ec87c94b`
and `rig tree … --store 4061ec87c94b --guards` against a store indexed by an older rig.

## Symptom

A command run against a specific `--store`/`--base`/`--head` that is schema-stale prints:

```
The store at C:\git\meddbase-analysis\.rig\a1a256c6ae6b-dirty\rig.db was built by an older rig
(schema mismatch: SQLite Error 1: 'no such column: c.EnclosingGuards'.).
```

The named store (`a1a256c6ae6b-dirty`) is **LATEST**, not the store actually queried (`4061ec87c94b`). Misleads
the user into re-indexing / distrusting the wrong store.

## Root cause

`CommandGuard.StoreError` (`src/Rig.Cli/CommandLine/CommandGuard.cs`) computes
`dbPath = StoreLayout.DbPath(workingDirectory)` — which resolves to the **default/LATEST** store — but the
failure is a mid-query `DbException` from whichever store the command actually opened. The open-time schema
gate (`SchemaGate.AssertReadableAsync`) does **not** check for `references.EnclosingGuards` (added with
control-dependence guards), so a stale store passes the gate and the missing column only surfaces mid-query
as a raw `DbException` with no store-path context — `StoreError` then guesses LATEST.

## Fix options

1. **Preferred:** extend `SchemaGate.AssertReadableAsync` to assert the expected columns/tables (incl.
   `EnclosingGuards`) at OPEN time → fails fast as `RigStoreException` (whose message renders directly and is
   store-correct because the gate knows the path). Removes the raw mid-query `DbException` path for schema drift.
2. Or thread the resolved store path into the `DbException` handler (wrap SQLite errors with the open store's
   path at the query layer) so `StoreError` reports the real store.

Not blocking; noted while bringing up the web impact view (which needs a schema-current base/head pair anyway).
