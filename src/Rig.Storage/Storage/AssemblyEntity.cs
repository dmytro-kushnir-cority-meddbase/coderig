namespace Rig.Storage.Storage;

// Registry of assemblies (projects) stored in this DB, keyed by assembly name. An assembly is stored
// ONCE regardless of how many solutions reference it (see docs/multi-solution-storage.md). ContentHash
// is a deterministic, order-independent digest of the assembly's source texts; re-indexing skips an
// assembly whose (AssemblyName, ContentHash) is already present.
public sealed class AssemblyEntity
{
    public string AssemblyName { get; set; } = "";

    public string ContentHash { get; set; } = "";

    public string IndexedAtUtcText { get; set; } = "";

    public int SymbolCount { get; set; }

    public int ReferenceCount { get; set; }

    // The solution whose index first contributed this assembly (provenance only — membership is the
    // authoritative solution↔assembly mapping; an assembly can belong to many solutions).
    public string SourceSolutionPath { get; set; } = "";
}
