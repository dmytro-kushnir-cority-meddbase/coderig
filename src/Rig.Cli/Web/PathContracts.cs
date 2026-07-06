namespace Rig.Cli.Web;

// JSON contracts for /api/path — the ordered concrete call-path steps between two symbols (web equivalent
// of `rig path <from> <to>`). Deliberately flat, camelCased by the ASP.NET default serializer, and decoupled
// from the internal Rig.Domain.PathStep so the wire shape can evolve independently of the engine — same
// convention as /api/tree's TreeNodeDto/TreeResponseDto (see WebContracts.cs). Reuses WebContracts' EffectDto
// for the per-node effects-if-handy annotation, so a path node's effect chip is IDENTICAL in shape to a tree
// node's.

internal sealed record PathNodeDto(
    string Id, // SymbolId (DocID) — same identity TreeNodeDto.Id uses, so a client can cross-link into /api/tree.
    string Name, // ShortName — display label.
    string EdgeKind, // how this hop was reached from the previous step ("entry"/"invocation"/"impl-dispatch"/…).
    string? LoopKind, // enclosing loop kind of the reaching call, if any (this hop fires inside a loop).
    int Fanout, // dispatch fan-out degree of the reaching edge (>1 = "could be any of these N", not a real call).
    string? HandoffVia, // async-handoff dispatcher id, when this hop crossed a `--async` handoff edge; else null.
    string? DispatchBasis, // "heuristic" = inferred dispatch (verify); null/"roslyn" = exact mined fact.
    string? File,
    int Line,
    // Effects-if-handy: the enclosing method's derived effects (same shape/derivation `/api/tree` reports),
    // empty when the method has none in this closure. Best-effort annotation, not part of the path itself.
    IReadOnlyList<EffectDto> Effects
);

internal sealed record PathResponseDto(
    string From,
    string To,
    // false = no concrete path found between From and To (mirrors the CLI's exit code 1); Nodes is then
    // empty and the client renders a "no path" state instead of an empty diagram.
    bool Matched,
    IReadOnlyList<PathNodeDto> Nodes,
    // Ambiguity disclosure (mirrors the CLI's AmbiguityNotice stderr note): the distinct symbols each
    // pattern resolved to. Count <= 1 = unambiguous; >1 = the path shown is just ONE of several a
    // multi-target pattern could have picked — the client can surface this the way `rig path` warns on stderr.
    IReadOnlyList<string> FromMatches,
    IReadOnlyList<string> ToMatches
);
