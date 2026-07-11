using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// FR-3 (RCA #2892), the n_plus_1 layer over the §#2892 looped_effect test in ProductionFixCorpusTests.
// rig already flags ANY effect inside a loop as looped_effect; FR-3 refines that for READS: a read inside a
// loop whose KEY ARGUMENT VARIES per iteration (the loop variable appears in the read's key) is an
// n+1 / read amplification (the Pathways 4000 queries/min defect — variable definitions read from the source
// PER ITERATION because they were missing from the cache). A read in the SAME loop with a CONSTANT key is
// hoistable and is NOT an n+1, so it must NOT fire — that contrast is the whole discriminator. The detector
// is data-driven (the read provider/operation set + the n_plus_1 rule live in builtin-rules.json); this test
// proves the shipped rules fire on the varying-key bug and stay silent on the constant-key fix.
public sealed class ProductionFixCorpusNPlusOneTests
{
    // System.Net.Http.HttpClient.GetStringAsync is a builtin http:GET read present in the framework refs —
    // a reliable read surface. The bug interpolates the loop variable into the URL ($"/var/{id}"), so the
    // key varies per iteration; the fix reads a single constant URL ("/vars/all"), hoistable out of the loop.
    [Test]
    public void _2892_looped_read_with_a_varying_key_fires_n_plus_1_the_constant_key_fix_does_not()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Pathways
            {
                public sealed class Interpreter
                {
                    // BUG (#2892): the key (id) varies per iteration -> a read per iteration -> N+1.
                    public async System.Threading.Tasks.Task ReadVars_Bug(
                        System.Net.Http.HttpClient client,
                        System.Collections.Generic.IEnumerable<string> ids)
                    {
                        foreach (var id in ids)
                        {
                            await client.GetStringAsync($"/var/{id}");
                        }
                    }

                    // FIX (#2892): same loop, but the key is CONSTANT -> hoistable -> NOT an N+1.
                    public async System.Threading.Tasks.Task ReadVars_Fix(
                        System.Net.Http.HttpClient client,
                        System.Collections.Generic.IEnumerable<string> ids)
                    {
                        foreach (var id in ids)
                        {
                            await client.GetStringAsync("/vars/all");
                        }
                    }
                }
            }
            """
        );

        // BUG: the http read is inside a loop (looped_effect) AND its key varies per iteration (n_plus_1).
        var bug = result.EffectsIn("ReadVars_Bug").Single(e => e.Provider == "http");
        bug.Observations.ShouldNotBeNull();
        bug.Observations!.ShouldContain(o => o.Type == "looped_effect");
        bug.Observations!.ShouldContain(o => o.Type == "n_plus_1");

        // FIX: still in a loop (looped_effect) — but the constant key is hoistable, so NO n_plus_1. This
        // contrast is the point: n_plus_1 is the read-amplification discriminator over plain looped_effect.
        var fix = result.EffectsIn("ReadVars_Fix").Single(e => e.Provider == "http");
        fix.Observations.ShouldNotBeNull();
        fix.Observations!.ShouldContain(o => o.Type == "looped_effect");
        result.ObservationsIn("ReadVars_Fix", "n_plus_1").ShouldBeEmpty();
    }
}
