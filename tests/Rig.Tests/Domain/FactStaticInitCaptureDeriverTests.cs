using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for the static_init_capture deriver: a config / Settings.* read whose ENCLOSING symbol is a
// STATIC field (an `F:` id present in the supplied staticFieldIds set) is flagged as frozen-at-type-init
// config. The grounding shape on the real store is exactly Read(resource = "…Settings.X", enclosing =
// "F:…StaticField"); these fixtures reproduce that shape and the discriminators around it.
public sealed class FactStaticInitCaptureDeriverTests
{
    private const string SettingsPattern = "MedDBase.Configuration.Settings.";

    // A shared_state read effect (the shape Settings.* reads take on the real store), enclosed by `enclosing`.
    private static DerivedEffect Read(string resourceType, string enclosing, int line = 141) =>
        new(
            Provider: "shared_state",
            Operation: "read",
            ResourceType: resourceType,
            EnclosingSymbolId: enclosing,
            FilePath: "ConceptView.cs",
            Line: line
        );

    private static StaticInitCaptureSpec Spec() => new(MutableSourcePatterns: [SettingsPattern]);

    private static IReadOnlySet<string> StaticFields(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Test]
    public void Flags_a_settings_read_enclosed_by_a_static_field()
    {
        // The live MedDBase example: Settings.ClinicalFormAutoComplete read into the static field
        // ConceptView.ClinicalFormConcept — frozen at type-init.
        const string field = "F:MedDBase.Views.Patient.Medical.ConceptView.ClinicalFormConcept";
        var effects = new List<DerivedEffect> { Read("P:MedDBase.Configuration.Settings.ClinicalFormAutoComplete", field) };

        var findings = FactStaticInitCaptureDeriver.Derive(effects, Spec(), StaticFields(field));

        findings.Count.ShouldBe(1);
        findings[0].Method.ShouldBe(field);
        findings[0].ResourceKey.ShouldBe("P:MedDBase.Configuration.Settings.ClinicalFormAutoComplete");
        findings[0].FilePath.ShouldBe("ConceptView.cs");
        findings[0].Line.ShouldBe(141);
    }

    [Test]
    public void Does_not_flag_a_settings_read_enclosed_by_a_NON_static_field()
    {
        // Same read shape, same `F:` enclosing — but the field is NOT in the static set (an instance field
        // re-runs its initializer on every construction, so the value is never frozen). Zero findings.
        const string field = "F:MedDBase.Views.Patient.Medical.ConceptView.InstanceConcept";
        var effects = new List<DerivedEffect> { Read("P:MedDBase.Configuration.Settings.ClinicalFormAutoComplete", field) };

        var findings = FactStaticInitCaptureDeriver.Derive(
            effects,
            Spec(),
            StaticFields( /* none */
            )
        );

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Does_not_flag_a_settings_read_enclosed_by_a_method_or_accessor()
    {
        // The read is live-evaluated each call when it sits in a getter/method (an `M:` enclosing), so it is
        // never frozen — even if that method id were (impossibly) handed in as "static". The `F:` prefix gate
        // is what excludes it. Zero findings.
        const string getter = "M:MedDBase.Views.Patient.Medical.ConceptView.get_Concept";
        var effects = new List<DerivedEffect> { Read("P:MedDBase.Configuration.Settings.ClinicalFormAutoComplete", getter) };

        var findings = FactStaticInitCaptureDeriver.Derive(effects, Spec(), StaticFields(getter));

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Does_not_flag_a_NON_settings_read_in_a_static_field_init()
    {
        // A static field initializer that reads something OTHER than a declared mutable source (here a plain
        // domain type, not Settings.*) is not in taint scope. The taint gate (mutable-source pattern) drops it.
        const string field = "F:MedDBase.Views.Patient.Medical.ConceptView.SomeStaticField";
        var effects = new List<DerivedEffect> { Read("MedDBase.Domain.Patient.PatientRecord", field) };

        var findings = FactStaticInitCaptureDeriver.Derive(effects, Spec(), StaticFields(field));

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Empty_mutable_sources_never_fires()
    {
        // Opt-in guard: with no patterns, even a real Settings read in a static field is not flagged.
        const string field = "F:MedDBase.Views.Patient.Medical.ConceptView.ClinicalFormConcept";
        var effects = new List<DerivedEffect> { Read("P:MedDBase.Configuration.Settings.ClinicalFormAutoComplete", field) };

        var findings = FactStaticInitCaptureDeriver.Derive(
            effects,
            new StaticInitCaptureSpec(MutableSourcePatterns: []),
            StaticFields(field)
        );

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Findings_are_deduped_and_stable_sorted_by_method_then_line()
    {
        // Two static fields each reading a Settings value (one twice, at different lines + a duplicate site).
        // Determinism: deduped by (method, resource, file, line) and ordered by (Method ordinal, Line).
        const string fieldA = "F:MedDBase.Configuration.Settings.UserDefaultSettings.MailMergeProvider";
        const string fieldB = "F:MedDBase.Application.Workflows.ReferralIncomming.ConfigurationData.daysToReview";
        var effects = new List<DerivedEffect>
        {
            Read("P:MedDBase.Configuration.Settings.SendGridProvider", fieldA, line: 272),
            Read("P:MedDBase.Configuration.Settings.DaysToReview", fieldB, line: 17),
            Read("P:MedDBase.Configuration.Settings.DaysToReview", fieldB, line: 16),
            Read("P:MedDBase.Configuration.Settings.DaysToReview", fieldB, line: 16), // exact duplicate site
        };

        var findings = FactStaticInitCaptureDeriver.Derive(effects, Spec(), StaticFields(fieldA, fieldB));

        // 3 distinct findings (the duplicate (fieldB, line 16) collapses).
        findings.Count.ShouldBe(3);
        // fieldB ("F:MedDBase.Application…") sorts before fieldA ("F:MedDBase.Configuration…") by ordinal;
        // within fieldB, line 16 precedes line 17.
        findings[0].Method.ShouldBe(fieldB);
        findings[0].Line.ShouldBe(16);
        findings[1].Method.ShouldBe(fieldB);
        findings[1].Line.ShouldBe(17);
        findings[2].Method.ShouldBe(fieldA);

        // Stable across input reordering.
        var reordered = new List<DerivedEffect>(((IEnumerable<DerivedEffect>)effects).Reverse());
        var again = FactStaticInitCaptureDeriver.Derive(reordered, Spec(), StaticFields(fieldA, fieldB));
        again.Select(f => (f.Method, f.Line)).ShouldBe(findings.Select(f => (f.Method, f.Line)));
    }
}
