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
        effects.Count(row => row[3] == "CoreAllocations.Payload" && row[4].Contains("CreateReferenceObjects")).ShouldBe(2);
        effects.Count(row => row[3] == "int[]" && row[4].Contains("CreateArrays")).ShouldBe(2);
        effects.Count(row => row[3] == "CoreAllocations.MarkerValue" && row[4].Contains("BoxValues")).ShouldBe(2);
        effects.Single(row => row[7] == "looped_effect")[4].ShouldContain("CreateInStructuralContexts");

        effects.ShouldNotContain(row => row[3].Contains("SmallValue", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[3].Contains("Span", StringComparison.Ordinal));
        effects.ShouldNotContain(row =>
            (row[3] == "string" || row[3] == "System.String") && row[4].Contains("ExerciseNegativeControls", StringComparison.Ordinal)
        );
        effects.ShouldNotContain(row => row[4].Contains("AttributeMetadataControl", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("MetadataValuesAttribute", StringComparison.Ordinal));
        effects.Count(row => row[8] == "object_creation").ShouldBe(5);
        effects.Count(row => row[8] == "array_creation").ShouldBe(2);
        effects.Count(row => row[8] == "boxing" && row[3] == "CoreAllocations.MarkerValue").ShouldBe(2);
        effects.Count(row => row[8] == "implicit_params").ShouldBe(1);
        effects.Count(row => row[8] == "delegate").ShouldBe(2);
        effects.Count(row => row[8] == "closure").ShouldBe(1);
        effects.Count(row => row[8] == "iterator_state_machine").ShouldBe(1);
        effects.Count(row => row[8] == "string_range").ShouldBe(1);
        effects.Count(row => row[8] == "string_concat").ShouldBe(1);
        effects.Count(row => row[8] == "string_interpolation").ShouldBe(1);
        effects.ShouldNotContain(row => row[4].Contains("CallWithExistingParamsArray", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("CallWithNoParamsArguments", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("SliceRawEndTagWithoutAllocation", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("ConstantConcatenation", StringComparison.Ordinal));
        effects.ShouldNotContain(row => row[4].Contains("ConstantInterpolation", StringComparison.Ordinal));

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

        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "CompilerLoweredScenarios.LoweredRun", "--only", "alloc", "--view", "full"],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);
        output.ToString().ShouldContain("[implicit_params");
        output.ToString().ShouldContain("[iterator_state_machine");
        output.ToString().ShouldContain("[string_range, conditional");
        output.ToString().ShouldContain("[string_concat");
        output.ToString().ShouldContain("[string_interpolation");
        output.ToString().ShouldContain("[delegate, cached_first_use");
    }

    private static IReadOnlyList<string[]> TsvRows(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t')).ToList();
}
