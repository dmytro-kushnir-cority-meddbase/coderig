namespace EntryPointEffects.Api.Services;

// Async-handoff fixture for the `callers --entrypoints` sync under-report test
// (CallersAsyncUnderreportTests). AuditSink.WriteAuditEntry is the query target: it is reached
// SYNCHRONOUSLY by NotificationsController.RecordDirect and, separately, ONLY across an event-subscription
// handoff (`publisher.Saved += OnSaved`) by NotificationsController.Subscribe. The subscription edge is
// sync-cut by default, so the sync entry-point answer omits Subscribe — the case the under-report footer warns about.
public sealed class AuditSink
{
    public void WriteAuditEntry() { }
}

public sealed class SavePublisher
{
    public event Action? Saved;

    public void Raise() => Saved?.Invoke();
}
