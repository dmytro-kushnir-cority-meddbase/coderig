namespace Rig.Storage;

// Thrown when a `.rig` store can't be read at the schema-gate open seam (SchemaGate.AssertReadableAsync):
// the store is uninitialized, or its index schema version doesn't match the one this `rig` writes. The
// `.rig` store is derived + disposable, so the message always points at the fix — re-index — never a
// migration. CLI top-level error handling renders the Message.
public sealed class RigStoreException(string message) : Exception(message);
