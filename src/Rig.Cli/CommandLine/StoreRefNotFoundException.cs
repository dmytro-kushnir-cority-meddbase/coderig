namespace Rig.Cli.CommandLine;

// Thrown when a `--store <ref>` (commit sha / short-sha / store-id) names a store that isn't indexed here.
// Carries the available store-ids so CommandGuard can render an actionable "indexed stores are: …" message
// rather than letting the bad path fall through to a raw SQLite open failure.
internal sealed class StoreRefNotFoundException(string storeRef, IReadOnlyList<string> available) : Exception
{
    internal string StoreRef { get; } = storeRef;

    internal IReadOnlyList<string> Available { get; } = available;
}
