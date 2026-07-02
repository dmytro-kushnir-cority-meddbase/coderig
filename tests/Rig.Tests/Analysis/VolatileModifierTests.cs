using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Shouldly;

namespace Rig.Tests.Analysis;

// `volatile` is mined into symbol_facts.Modifiers (2026-07-02) so the lazy_init_race lock-enclosed tier
// can corroborate a safe DCL (FactHazardDeriver: volatile cell + publish-last => suppress). A store
// indexed before this yields no volatile modifier — the hazard layer treats that as never-corroborated.
public sealed class VolatileModifierTests
{
    private static FactExtractionResult Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            "Snippet",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        return FactExtractor.Extract(new SourceModel("Snippet", "Snippet.cs", tree, tree.GetRoot(), model), new SymbolStringCache());
    }

    [Test]
    public void A_volatile_field_carries_the_volatile_modifier_and_a_plain_field_does_not()
    {
        var facts = Extract(
            """
            namespace App
            {
                public sealed class Config
                {
                    private static volatile Config _instance;
                    private static Config _plain;
                }
            }
            """
        );

        var byId = facts.Symbols.ToDictionary(s => s.SymbolId, s => s.Modifiers);
        byId["F:App.Config._instance"].Split(' ').ShouldContain("volatile");
        byId["F:App.Config._plain"].Split(' ').ShouldNotContain("volatile");
    }
}
