using EntryPointEffects.Api.Data;
using EntryPointEffects.Api.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AppDb"))
);
builder.Services.AddHttpClient<BillingClient>();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!)
);
builder.Services.AddScoped<TeamWorkflow>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();

var app = builder.Build();

app.MapGet(
    "/minapi/teams/{id}",
    async (int id, TeamWorkflow workflow) =>
    {
        return await workflow.LoadTeamSummaryAsync(id);
    }
);

app.MapPost(
    "/minapi/teams",
    async (CreateTeamRequest request, TeamWorkflow workflow) =>
    {
        await workflow.CreateTeamAsync(request.Name);
        return Results.Accepted();
    }
);

app.MapGet("/minapi/cycles/self", () => CycleFixture.SelfRecursive(2));
app.MapGet("/minapi/cycles/mutual", () => CycleFixture.MutualA(2));
app.MapGet("/minapi/cycles/three-step", () => CycleFixture.ThreeStepA(2));

app.MapControllers();

app.Run();

public sealed record CreateTeamRequest(string Name);
