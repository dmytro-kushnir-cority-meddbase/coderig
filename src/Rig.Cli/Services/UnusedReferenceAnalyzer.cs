namespace Rig.Cli.Services;

// Pure graph-algebra for `rig refs --unused` / `--usage`: diff the DECLARED <ProjectReference> graph (parsed
// from the .csproj files, no MSBuild) against the OBSERVED first-party usage edges (mined from the fact
// store), yielding the declared assembly edges with zero symbol usage — candidate prunable references.
//
// Everything here is a PURE function of plain in-memory inputs (no DB, no filesystem read) so it is
// unit-testable with hand-built graphs. The command layer does the IO (DependencyGraph + store reads) then
// calls these. Paths follow the same owning-directory + modal-assembly technique as DeploymentMap.
internal static class UnusedReferenceAnalyzer
{
    // Assign each indexed file to its owning csproj by LONGEST owning-directory prefix (a nested project wins
    // over its parent), then set each csproj's assembly to the MODAL (most frequent) DefiningAssembly among
    // the files it owns. Csprojs with no owned indexed files get NO entry (they were not indexed, so they
    // have no assembly and are excluded from the diff). Ties on frequency break by assembly name (Ordinal) so
    // the result is deterministic. The returned map is keyed by the SAME csproj path strings passed in, so it
    // lines up with the declared-graph keys FindUnused consumes.
    internal static IReadOnlyDictionary<string, string> BuildCsprojToAssembly(
        IReadOnlyList<string> csprojPaths,
        IReadOnlyList<(string FilePath, string Assembly)> files
    )
    {
        // Owning-dir prefixes, longest first (mirror DeploymentMap: EnsureTrailingSeparator, longest wins).
        var csprojDirs = csprojPaths
            .Select(p => (Dir: EnsureTrailingSeparator(SafeDirectory(p)), Csproj: p))
            .OrderByDescending(x => x.Dir.Length)
            .ToArray();

        // csproj -> (assembly -> owned-file count)
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, assembly) in files)
        {
            if (string.IsNullOrEmpty(assembly) || string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(filePath);
            }
            catch
            {
                continue;
            }

            foreach (var (dir, csproj) in csprojDirs)
            {
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!counts.TryGetValue(csproj, out var byAsm))
                {
                    byAsm = new Dictionary<string, int>(StringComparer.Ordinal);
                    counts[csproj] = byAsm;
                }

                byAsm[assembly] = byAsm.GetValueOrDefault(assembly) + 1;
                break;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (csproj, byAsm) in counts)
        {
            var modal = byAsm.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            result[csproj] = modal;
        }

        return result;
    }

    // The candidate-prunable diff: for each DECLARED csproj edge A_csproj -> B_csproj, map both endpoints to
    // their assemblies. KEEP the edge only when BOTH endpoints have a known assembly (both were indexed) and
    // the assemblies differ (self-edges — same assembly split across csprojs — are not references). A
    // CANDIDATE is a declared assembly edge (asm(A), asm(B)) that does NOT appear in the observed usage-edge
    // set. Result is sorted by declaring then unused assembly (Ordinal), deduped.
    internal static IReadOnlyList<(string DeclaringAsm, string UnusedAsm)> FindUnused(
        IReadOnlyDictionary<string, List<string>> declaredCsprojGraph,
        IReadOnlyDictionary<string, string> csprojToAsm,
        ISet<(string, string)> usageEdges
    )
    {
        var declaredAsmEdges = new HashSet<(string, string)>();
        foreach (var (fromCsproj, toList) in declaredCsprojGraph)
        {
            if (!csprojToAsm.TryGetValue(fromCsproj, out var fromAsm))
            {
                continue;
            }

            foreach (var toCsproj in toList)
            {
                if (!csprojToAsm.TryGetValue(toCsproj, out var toAsm) || string.Equals(fromAsm, toAsm, StringComparison.Ordinal))
                {
                    continue;
                }

                declaredAsmEdges.Add((fromAsm, toAsm));
            }
        }

        return declaredAsmEdges
            .Where(edge => !usageEdges.Contains(edge))
            .OrderBy(edge => edge.Item1, StringComparer.Ordinal)
            .ThenBy(edge => edge.Item2, StringComparer.Ordinal)
            .Select(edge => (DeclaringAsm: edge.Item1, UnusedAsm: edge.Item2))
            .ToList();
    }

    private static string SafeDirectory(string csprojPath)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(csprojPath)) ?? csprojPath;
        }
        catch
        {
            return csprojPath;
        }
    }

    private static string EnsureTrailingSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
}
