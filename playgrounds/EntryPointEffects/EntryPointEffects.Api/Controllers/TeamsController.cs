using EntryPointEffects.Api.Data;
using EntryPointEffects.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EntryPointEffects.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TeamsController : ControllerBase
{
    private readonly TeamWorkflow _workflow;
    private readonly ITeamRepository _repository;

    public TeamsController(TeamWorkflow workflow, ITeamRepository repository)
    {
        _workflow = workflow;
        _repository = repository;
    }

    [HttpGet("{id}")]
    public async Task<object> Get(int id)
    {
        return await _workflow.LoadTeamSummaryAsync(id);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateTeamRequest request)
    {
        await _workflow.CreateTeamAsync(request.Name);
        return Accepted();
    }

    // Single-implementation dispatch test fixture:
    // _repository is ITeamRepository — rig should resolve via DI to TeamRepository
    // and link EF Core effects from TeamRepository.GetAllAsync / TeamRepository.AddAsync.
    [HttpGet("via-interface")]
    public async Task<IActionResult> ListViaInterface()
    {
        var teams = await _repository.GetAllAsync();
        return Ok(teams);
    }

    [HttpPost("via-interface")]
    public async Task<IActionResult> CreateViaInterface(CreateTeamRequest request)
    {
        await _repository.AddAsync(new Team { Name = request.Name });
        return Accepted();
    }

    // Method-group delegate test fixture:
    // _repository.GetAllAsync is passed as a method group to Task.Run (not a lambda).
    // The method group scan should follow it to TeamRepository.GetAllAsync → EF Core effect.
    [HttpGet("via-method-group")]
    public async Task<IActionResult> ListViaMethodGroup()
    {
        var teams = await Task.Run(_repository.GetAllAsync);
        return Ok(teams);
    }
}
