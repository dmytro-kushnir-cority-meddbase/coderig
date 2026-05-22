using EntryPointEffects.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EntryPointEffects.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TeamsController : ControllerBase
{
    private readonly TeamWorkflow _workflow;

    public TeamsController(TeamWorkflow workflow)
    {
        _workflow = workflow;
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
}
