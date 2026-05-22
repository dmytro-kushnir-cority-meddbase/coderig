using EntryPointEffects.Api.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace EntryPointEffects.Api.Services;

public sealed class TeamWorkflow
{
    private readonly AppDbContext _db;
    private readonly BillingClient _billingClient;
    private readonly IDatabase _redis;

    public TeamWorkflow(AppDbContext db, BillingClient billingClient, IConnectionMultiplexer redis)
    {
        _db = db;
        _billingClient = billingClient;
        _redis = redis.GetDatabase();
    }

    public async Task<object> LoadTeamSummaryAsync(int teamId)
    {
        var teams = await _db.Teams.ToListAsync();
        var cached = await _redis.StringGetAsync($"team:{teamId}");
        var invoice = await _billingClient.LoadInvoiceAsync(teamId);
        ObserveDynamicBoundary(invoice);
        var relatedTeamIds = new[] { teamId, teamId + 1 };

        foreach (var relatedTeamId in relatedTeamIds)
        {
            await _redis.StringGetAsync($"team:{relatedTeamId}");
        }

        await _billingClient.LoadInvoicesAsync(relatedTeamIds);

        return new
        {
            TeamCount = teams.Count,
            Cached = cached.ToString(),
            Invoice = invoice
        };
    }

    public async Task CreateTeamAsync(string name)
    {
        _db.Teams.Add(new Team { Name = name });
        await _db.SaveChangesAsync();
        await _redis.StringSetAsync($"team:{name}", name);
    }

    private static void ObserveDynamicBoundary(dynamic value)
    {
        value.UnresolvedRuntimeCall();
    }
}
