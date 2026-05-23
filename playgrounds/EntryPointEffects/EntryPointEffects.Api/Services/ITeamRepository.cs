using EntryPointEffects.Api.Data;

namespace EntryPointEffects.Api.Services;

// Single-implementation dispatch test fixture.
// ITeamRepository is registered with exactly one concrete implementation (TeamRepository).
// rig should resolve interface calls to TeamRepository via single-impl dispatch
// and link EF Core effects that originate inside TeamRepository methods.
public interface ITeamRepository
{
    Task<List<Team>> GetAllAsync();
    Task<Team?> FindAsync(int id);
    Task AddAsync(Team team);
}
