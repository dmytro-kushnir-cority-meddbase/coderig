using Rig.Cli.Commands;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// ClassifyStructuralCause: why a STRUCTURAL-ONLY entry point (its reachable tree changed but its effect set did
// NOT) shows up — the cause buckets behind the demoted structural breadcrumb. The classifier is proportional:
// data-shape (fields/properties/accessors/ctors) dominating the moved+changed members reads as RecordShape even
// when a stray real method moved alongside (the dominant noise of a record field add), while genuine method-level
// churn lands in Other for review. These cases mirror the live MR validation (the HealthcodeSettings migration:
// hundreds of field-ripple EPs, each with at most an incidental deleted-deserializer method → RecordShape).
public sealed class ImpactStructuralCauseTests
{
    // Build an EpReachDelta with only the fields the classifier reads (stems + in-place count); the rest are
    // display/ranking fields irrelevant here.
    private static ImpactCommand.EpReachDelta Delta(
        string[]? added = null,
        string[]? removed = null,
        string[]? changed = null,
        int inPlace = 0
    ) =>
        new(
            Kind: "action",
            Route: "X/Y.Z",
            FilePath: "C:/repo/X.cs",
            Line: 1,
            Requires: null,
            Added: [],
            Removed: [],
            AddedStems: added ?? [],
            RemovedStems: removed ?? [],
            ChangedStems: changed ?? [],
            DistinctStemDelta: (added?.Length ?? 0) + (removed?.Length ?? 0) + (changed?.Length ?? 0),
            InPlaceCount: inPlace
        );

    [Test]
    public void Pure_field_property_ripple_is_RecordShape()
    {
        // A record gained Healthcode fields: every reaching EP sees the new accessors + the `R:` access nodes.
        var d = Delta(
            added:
            [
                "MedDBase.DataAccessTier.EntityClasses.CompanyEntityBase.get_HealthcodeInsurerCode",
                "R:P:MedDBase.DataAccessTier.EntityClasses.CompanyEntityBase.HealthcodeInsurerCode",
                "R:F:MedDBase.DataAccessTier.EntityClasses.MpRecord.HealthcodeRegistered",
            ]
        );

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.RecordShape);
    }

    [Test]
    public void Field_ripple_with_one_incidental_method_move_stays_RecordShape()
    {
        // The live-MR case: ~12 data-shape moves + the single deleted object-store deserializer. The CAUSE is
        // still the one record/settings-type change, so the lone method move must not flip it to Other.
        var d = Delta(
            added:
            [
                "CompanyEntityBase.get_HealthcodeInsurerCode",
                "CompanyEntityBase.get_HealthcodeSourceKind",
                "MpEntityBase.get_HealthcodeOverridePayee",
                "MpEntityBase.get_HealthcodeRegistered",
                "SiteEntityBase.get_HealthcodeOverridePayee",
                "R:P:CompanyEntityBase.HealthcodeInsurerCode",
                "R:P:CompanyEntityBase.HealthcodeSourceKind",
                "R:P:MpEntityBase.HealthcodeOverridePayee",
                "R:F:MpRecord.HealthcodeRegistered",
            ],
            removed:
            [
                "R:P:HealthcodeSettings.Companies",
                "R:P:HealthcodeSettings.MedicalPeople",
                "MedDBase.Application.Workflows.InvoiceDebtChase.HealthcodeSettings.OnDeserialised", // the one real method
            ]
        );

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.RecordShape);
    }

    [Test]
    public void Mostly_method_churn_is_Other()
    {
        // A genuine refactor/migration site: real methods dominate the move, only one data-shape node rides along.
        // This is the bucket a reviewer must look at (a migration can move reach with no net-new effect kind).
        var d = Delta(
            added: ["App.Service.NewHandler", "App.Service.NewValidator", "App.Service.NewMapper"],
            removed: ["App.Service.OldHandler", "App.Service.OldValidator", "R:P:App.Record.SomeField"]
        );

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.Other);
    }

    [Test]
    public void Purely_constructor_signatures_is_CtorSig()
    {
        // A record's ctor params moved and nothing else — no new accessor, just a re-signing of the ctor.
        var d = Delta(changed: ["App.CompanyRecord.#ctor", "App.SiteRecord.#ctor"]);

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.CtorSig);
    }

    [Test]
    public void No_structural_move_but_in_place_body_change_is_InPlace()
    {
        // Phase-2 signal only: a reachable method's body changed (a constant edit) with no add/remove/re-sign.
        var d = Delta(inPlace: 3);

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.InPlace);
    }

    [Test]
    public void FqnForCard_resolves_site_to_dotted_fqn_stripping_params()
    {
        // The (file,line) site maps to a method DocID -> the card shows the param-free dotted FQN, NOT the
        // path-style route. This is what makes the behavioral + structural EP labels round-trip into `rig tree`.
        var idBySite = new Dictionary<(string, int), string>
        {
            [("C:/repo/Patient/Medical/ObservationRequest.cs", 224)] = "M:MedDBase.Pages.Patient.Medical.ObservationRequest.Save(System.Boolean)",
        };

        var label = ImpactCommand.FqnForCard(
            route: "Patient/Medical/ObservationRequest.Save", // the path-style route must NOT win
            filePath: "C:/repo/Patient/Medical/ObservationRequest.cs",
            line: 224,
            idBySite: idBySite
        );

        label.ShouldBe("MedDBase.Pages.Patient.Medical.ObservationRequest.Save");
    }

    [Test]
    public void FqnForCard_falls_back_to_route_when_site_unresolved()
    {
        // No site match (synthesized/promoted EP, or empty file) -> keep the derived route so the card is never
        // blank. The diff still keys on (Kind, Route) regardless.
        var empty = new Dictionary<(string, int), string>();

        ImpactCommand
            .FqnForCard(route: "background/Some/Route.Tick", filePath: "", line: 0, idBySite: empty)
            .ShouldBe("background/Some/Route.Tick");
        ImpactCommand
            .FqnForCard(route: "X/Y.Z", filePath: "C:/repo/X.cs", line: 99, idBySite: empty) // file present but no (file,line) hit
            .ShouldBe("X/Y.Z");
    }

    [Test]
    public void Ctor_counts_as_data_shape_alongside_field_adds()
    {
        // A field add shows up as new accessors AND a changed ctor signature; together they're all data-shape.
        var d = Delta(
            added: ["App.CompanyEntityBase.get_HealthcodeInsurerCode", "R:P:App.CompanyEntityBase.HealthcodeInsurerCode"],
            changed: ["App.CompanyRecord.#ctor"]
        );

        ImpactCommand.ClassifyStructuralCause(d).ShouldBe(ImpactCommand.StructuralCause.RecordShape);
    }
}
