using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Shouldly;

namespace Rig.Tests.Storage;

// Slice 2 of the multi-solution store (docs/multi-solution-storage.md): Writes.SaveAsync populates the
// assembly registry (content-addressed) and solution membership alongside the run-scoped facts.
public sealed class AssemblyRegistryTests
{
    private static SymbolFact Symbol(string id, string assembly) =>
        new(id, "method", id, "Ns", null, "public", "", id + "()", "F.cs", 1, assembly, false);

    private static ReferenceFact Invocation(string target, string enclosing) =>
        new(target, "invocation", enclosing, "Other.Asm", true, "F.cs", 2);

    private static AnalysisResult Result(string solutionPath, IReadOnlyList<SymbolFact> symbols, IReadOnlyList<ReferenceFact> references) =>
        new(solutionPath, [], [], Symbols: symbols, References: references);

    [Test]
    public async Task SaveAsync_populates_assembly_registry_and_membership()
    {
        var dir = Directory.CreateTempSubdirectory("rig-asmreg-").FullName;
        var dbPath = Path.Combine(dir, "rig.db");
        var solution = Path.Combine(dir, "Test.slnx");
        try
        {
            var symbols = new[]
            {
                Symbol("M:Asm.A.One", "Asm.A"),
                Symbol("M:Asm.A.Two", "Asm.A"),
                Symbol("M:Asm.B.Solo", "Asm.B"),
            };
            var references = new[] { Invocation("M:Asm.B.Solo", "M:Asm.A.One") };

            await using (var write = new RigDbContext(dbPath, pooling: false))
                await Writes.SaveAsync(write, Result(solution, symbols, references));

            string hashABefore;
            await using (var read = new RigDbContext(dbPath, pooling: false))
            {
                var assemblies = await read.Assemblies.AsNoTracking().OrderBy(a => a.AssemblyName).ToListAsync();
                assemblies.Select(a => a.AssemblyName).ShouldBe(["Asm.A", "Asm.B"]);

                var a = assemblies.Single(x => x.AssemblyName == "Asm.A");
                a.SymbolCount.ShouldBe(2);
                a.ReferenceCount.ShouldBe(1); // the invocation enclosed in Asm.A.One
                a.ContentHash.ShouldNotBeNullOrEmpty();
                a.SourceSolutionPath.ShouldBe(Path.GetFullPath(solution));
                hashABefore = a.ContentHash;

                var b = assemblies.Single(x => x.AssemblyName == "Asm.B");
                b.SymbolCount.ShouldBe(1);
                b.ReferenceCount.ShouldBe(0);

                var membership = await read
                    .SolutionMemberships.AsNoTracking()
                    .Where(m => m.SolutionPath == Path.GetFullPath(solution))
                    .Select(m => m.AssemblyName)
                    .OrderBy(n => n)
                    .ToListAsync();
                membership.ShouldBe(["Asm.A", "Asm.B"]);
            }

            // Re-indexing the SAME content is idempotent: no duplicate assemblies/membership, same hash.
            await using (var write = new RigDbContext(dbPath, pooling: false))
                await Writes.SaveAsync(write, Result(solution, symbols, references));

            await using (var read = new RigDbContext(dbPath, pooling: false))
            {
                (await read.Assemblies.CountAsync()).ShouldBe(2);
                (await read.SolutionMemberships.CountAsync()).ShouldBe(2);
                (await read.Assemblies.AsNoTracking().SingleAsync(a => a.AssemblyName == "Asm.A")).ContentHash.ShouldBe(hashABefore);
            }

            // A content change (extra symbol in Asm.A) flips that assembly's hash.
            var grown = symbols.Append(Symbol("M:Asm.A.Three", "Asm.A")).ToArray();
            await using (var write = new RigDbContext(dbPath, pooling: false))
                await Writes.SaveAsync(write, Result(solution, grown, references));

            await using (var read = new RigDbContext(dbPath, pooling: false))
            {
                var a = await read.Assemblies.AsNoTracking().SingleAsync(x => x.AssemblyName == "Asm.A");
                a.ContentHash.ShouldNotBe(hashABefore);
                a.SymbolCount.ShouldBe(3);
                (await read.Assemblies.CountAsync()).ShouldBe(2); // still just A and B
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Merging_a_second_solution_accumulates_assemblies_and_per_solution_membership()
    {
        var dir = Directory.CreateTempSubdirectory("rig-asmreg-merge-").FullName;
        var dbPath = Path.Combine(dir, "rig.db");
        var master = Path.Combine(dir, "Master.slnx");
        var tail = Path.Combine(dir, "Tail.slnx");
        try
        {
            // Base solution (like the master mine).
            await using (var write = new RigDbContext(dbPath, pooling: false))
                await Writes.SaveAsync(write, Result(master, [Symbol("M:Core.A", "Core"), Symbol("M:Shared.S", "Shared")], []));

            // A second solution merged in (append, no atomic-replace): brings a new assembly (Tail) and
            // re-references a shared one (Shared, already present from the master).
            await using (var write = new RigDbContext(dbPath, pooling: false))
                await Writes.SaveAsync(write, Result(tail, [Symbol("M:Tail.T", "Tail"), Symbol("M:Shared.S", "Shared")], []));

            await using (var read = new RigDbContext(dbPath, pooling: false))
            {
                // Registry accumulated all three assemblies (Shared deduped to one row).
                var names = await read.Assemblies.AsNoTracking().Select(a => a.AssemblyName).OrderBy(n => n).ToListAsync();
                names.ShouldBe(["Core", "Shared", "Tail"]);

                // Membership is per-solution: each solution lists exactly the assemblies it contains,
                // including the shared one.
                var masterMembers = await Members(read, master);
                masterMembers.ShouldBe(["Core", "Shared"]);
                var tailMembers = await Members(read, tail);
                tailMembers.ShouldBe(["Shared", "Tail"]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static async Task<List<string>> Members(RigDbContext ctx, string solution) =>
            await ctx
                .SolutionMemberships.AsNoTracking()
                .Where(m => m.SolutionPath == Path.GetFullPath(solution))
                .Select(m => m.AssemblyName)
                .OrderBy(n => n)
                .ToListAsync();
    }
}
