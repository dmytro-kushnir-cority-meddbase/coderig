using Rig.Cli.Deployments;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// The per-deployment capability gate: an entry point is ACTIVE-IN a service only when the service
// `provides` one of the tokens the EP's rule `requires` (ANY / non-empty-intersection). A null/empty
// requirement is ungated, so active-in collapses to loaded-in (full backward compatibility).
public sealed class DeploymentGateTests
{
    private static DeploymentMap MapOf(params ServiceDef[] services) =>
        new(services, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase), []);

    private static ServiceDef Service(string name, params string[] provides) => new(name, $"{name}.csproj", "iis", null, provides);

    [Test]
    public void Ungated_entry_point_is_active_in_every_loaded_service()
    {
        var map = MapOf(Service("Front", "FrontEnd"), Service("Data", "DataServer"));
        var loaded = new[] { "Front", "Data" };

        map.ActiveServices(loaded, null).ShouldBe(loaded); // null = ungated
        map.ActiveServices(loaded, []).ShouldBe(loaded); // empty = ungated
    }

    [Test]
    public void Gated_entry_point_is_active_only_in_a_providing_service()
    {
        var map = MapOf(Service("Front", "FrontEnd", "BackEnd"), Service("Data", "DataServer"));
        var loaded = new[] { "Front", "Data" };

        // Requires FrontEnd: Front provides it, Data does not -> active only in Front (the stale-actor case).
        map.ActiveServices(loaded, ["FrontEnd"]).ShouldBe(["Front"]);
    }

    [Test]
    public void Any_semantics_a_single_overlapping_token_activates()
    {
        var map = MapOf(Service("Front", "FrontEnd"), Service("Import", "Import"));
        var loaded = new[] { "Front", "Import" };

        // Rule requires any of {BackEnd, Import}: only Import overlaps -> active in Import.
        map.ActiveServices(loaded, ["BackEnd", "Import"]).ShouldBe(["Import"]);
    }

    [Test]
    public void Gated_out_of_every_loaded_service_yields_empty()
    {
        var map = MapOf(Service("Data", "DataServer"));
        map.ActiveServices(["Data"], ["FrontEnd"]).ShouldBeEmpty();
    }

    [Test]
    public void A_service_with_no_provides_never_activates_a_gated_entry_point()
    {
        var map = MapOf(Service("Legacy")); // no provides declared
        map.ActiveServices(["Legacy"], ["FrontEnd"]).ShouldBeEmpty();
        map.ActiveServices(["Legacy"], null).ShouldBe(["Legacy"]); // but ungated still loads
    }
}
