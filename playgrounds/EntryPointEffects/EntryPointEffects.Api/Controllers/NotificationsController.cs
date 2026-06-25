using EntryPointEffects.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EntryPointEffects.Api.Controllers;

// Two entry points reach AuditSink.WriteAuditEntry: RecordDirect synchronously, Subscribe only via an
// event-subscription handoff (sync-cut). Drives the `callers WriteAuditEntry --entrypoints` under-report test.
[ApiController]
[Route("api/[controller]")]
public sealed class NotificationsController : ControllerBase
{
    private readonly AuditSink _sink;
    private readonly SavePublisher _publisher;

    public NotificationsController(AuditSink sink, SavePublisher publisher)
    {
        _sink = sink;
        _publisher = publisher;
    }

    // Reaches AuditSink.WriteAuditEntry SYNCHRONOUSLY — the sync entry-point answer.
    [HttpPost("direct")]
    public IActionResult RecordDirect()
    {
        _sink.WriteAuditEntry();
        return Accepted();
    }

    // Reaches AuditSink.WriteAuditEntry ONLY via the event-subscription handoff (`+= OnSaved`), which is
    // sync-cut — so this EP surfaces only under --async.
    [HttpPost("subscribe")]
    public IActionResult Subscribe()
    {
        _publisher.Saved += OnSaved;
        return Accepted();
    }

    private void OnSaved() => _sink.WriteAuditEntry();
}
