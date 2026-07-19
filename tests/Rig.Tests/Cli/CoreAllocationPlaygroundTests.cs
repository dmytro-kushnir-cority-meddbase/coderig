using Rig.Cli;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

public sealed class CoreAllocationPlaygroundTests
{
    [Test]
    public async Task Core_allocations_are_derived_and_bounded_from_the_owned_playground()
    {
        using var playground = await TempPlayground.CreateCoreAllocationsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["derive", "--format", "tsv"], output, error, workingDirectory)).ShouldBe(0);

        var effects = TsvRows(output.ToString()).Where(row => row[0] == "effect").ToList();
        effects.Count.ShouldBe(9);
        effects.Count(row => row[1] == "alloc" && row[2] == "object").ShouldBe(5);
        effects.Count(row => row[1] == "alloc" && row[2] == "array").ShouldBe(2);
        effects.Count(row => row[1] == "alloc" && row[2] == "boxing").ShouldBe(2);
        effects.Count(row => row[3] == "CoreAllocations.Payload" && row[4].Contains("CreateReferenceObjects")).ShouldBe(2);
        effects.Count(row => row[3] == "int[]" && row[4].Contains("CreateArrays")).ShouldBe(2);
        effects.Count(row => row[3] == "CoreAllocations.MarkerValue" && row[4].Contains("BoxValues")).ShouldBe(2);
        effects.Single(row => row[7] == "looped_effect")[4].ShouldContain("CreateInStructuralContexts");

        effects.ShouldNotContain(row => row[3].Contains("SmallValue", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[3].Contains("Span", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[3] == "string" || row[3] == "System.String");
        effects.ShouldNotContain(row => row[4].Contains("AttributeMetadataControl", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("MetadataValuesAttribute", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("CompilerLoweredScenarios", StringComparison.Ordinal));

        await using (var context = new RigDbContext(StoreLayout.DbPath(workingDirectory), pooling: false, readOnly: true))
        {
            var allocationFacts = await Reads.LoadAllocationFactsAsync(context);
            var guarded = allocationFacts.Single(fact =>
                fact.EnclosingSymbolId.Contains("CreateInStructuralContexts", StringComparison.Ordinal)
                && fact.EnclosingGuards is not null
                && FactStructuralContext
                    .DecodeGuards(fact.EnclosingGuards)
                    .Any(guard => guard.Predicate.Contains("enabled", StringComparison.Ordinal))
            );
            FactStructuralContext
                .DecodeGuards(guarded.EnclosingGuards)
                .ShouldContain(guard => guard.Predicate.Contains("enabled", StringComparison.Ordinal));
        }

        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "AllocationScenarios.Run", "--only", "alloc", "--view", "effects", "--format", "tsv", "--guards"],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);

        var tree = TsvRows(output.ToString());
        tree.Single(row => row[1].Contains("CreateReferenceObjects", StringComparison.Ordinal))[5].ShouldBe("alloc:object,alloc:object");
        tree.Single(row => row[1].Contains("CreateArrays", StringComparison.Ordinal))[5].ShouldBe("alloc:array,alloc:array");
        tree.Single(row => row[1].Contains("BoxValues", StringComparison.Ordinal))[5].ShouldBe("alloc:boxing,alloc:boxing");
        tree.Single(row => row[1].Contains("CreateInStructuralContexts", StringComparison.Ordinal))[5]
            .ShouldBe("alloc:object,alloc:object");
        tree.Single(row => row[1].Contains("ExerciseNegativeControls", StringComparison.Ordinal))[5].ShouldBeEmpty();
        tree.ShouldNotContain(row => row[1].Contains("Unreachable", StringComparison.Ordinal));
        tree.ShouldNotContain(row => row[1].Contains("UnreachablePayload", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string[]> TsvRows(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t')).ToList();
}
