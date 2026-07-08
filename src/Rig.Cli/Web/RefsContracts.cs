namespace Rig.Cli.Web;

// JSON contract for /api/refs/unused + /api/refs/usage — the web equivalent of `rig refs --unused` /
// `--usage`. Same data as the CLI (via UnusedRefsQueryService), reshaped for the SPA: unused candidates are
// pre-grouped by declaring assembly (sorted), matching the CLI's grouped human render.

// One declaring assembly and the assemblies it references but never uses (candidate prunable references).
internal sealed record RefsUnusedGroupDto(string DeclaringAsm, IReadOnlyList<string> UnusedAsms);

internal sealed record RefsUnusedResponseDto(
    // False when the indexed solution's .csproj files are unavailable (re-index / run from the store dir).
    bool SolutionAvailable,
    IReadOnlyList<RefsUnusedGroupDto> Groups,
    int CandidateCount,
    int ProjectCount
);

// One assembly's inbound first-party usage: total references + the number of distinct methods making them.
internal sealed record RefsUsageRowDto(string Assembly, int Refs, int FromMethods);

// Rows are ascending by Refs (least-used first) — the order UnusedRefsQueryService.UsageAsync returns.
internal sealed record RefsUsageResponseDto(IReadOnlyList<RefsUsageRowDto> Rows);
