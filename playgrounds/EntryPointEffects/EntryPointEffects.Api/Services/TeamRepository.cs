using EntryPointEffects.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EntryPointEffects.Api.Services;

public sealed class TeamRepository : ITeamRepository
{
    private readonly AppDbContext _db;

    public TeamRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<Team>> GetAllAsync() => _db.Teams.ToListAsync();

    public Task<Team?> FindAsync(int id) => _db.Teams.FindAsync(id).AsTask();

    public async Task AddAsync(Team team)
    {
        _db.Teams.Add(team);
        await _db.SaveChangesAsync();
    }
}
