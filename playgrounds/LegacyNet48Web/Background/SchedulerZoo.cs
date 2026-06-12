using System;
using System.Threading.Tasks;
using Echo;
using LegacyNet48Web.Entities;
using LegacyNet48Web.External;
using MedDBase.Application.Core.Background;

namespace LegacyNet48Web.Background
{
    // The async-handoff dispatcher ZOO (see docs/ASYNC-FLOW-PLAN.md). Each registrar hands a
    // METHOD-GROUP to a dispatcher that runs it later / on another thread — so the registrar->callback
    // edge is an async HANDOFF, not a synchronous call. The callbacks do real DB/SOAP work, so the
    // sync-cut vs --async difference is observable: sync-cut must NOT show those effects reachable from
    // RegisterSchedules; --async restores them tagged; `callers --roots` lists each callback as an
    // origin. ProcessHealthcodeQueue mirrors the headline MedDBase case.
    public class SchedulerZoo
    {
        private readonly DataAdapter _db;
        private readonly HealthcodeServiceProxy _soap;

        public SchedulerZoo(DataAdapter db, HealthcodeServiceProxy soap)
        {
            _db = db;
            _soap = soap;
        }

        // The registration site. NOTHING below runs synchronously here — every callback is scheduled.
        public void RegisterSchedules(IAsyncEvent<string> changed)
        {
            // M1 repeating background schedule — classified `background`.
            new RepeatingBackgroundProcessSchedule(TimeSpan.FromMinutes(5), ProcessHealthcodeQueue, "healthcode-queue");
            // M2 one-shot schedule — classified `background`.
            new BackgroundProcessSchedule(DateTime.Now, CleanupExpired, "cleanup");
            // M5 AsyncEvent subscriber — classified `event`.
            changed.Add(OnChamberChanged);
            // M6 Echo actor spawn — classified `actor`.
            Process.spawn<string>("worker", HandleMessage);
            // Fire-and-forget Task.Run: the BCL dispatcher edge is filtered out of the first-party call
            // graph, so co-location can't classify it — BackgroundWork stays an UNCLASSIFIED methodGroup
            // residual (its body is still reachable; documents the BCL/lambda residual).
            Task.Run(BackgroundWork);

            // --- Convoluted MULTI-LINE layouts (regression net for the structural DelegateConsumer
            // resolution). Each delegate below must classify as a handoff regardless of HOW FAR the
            // `new`/call is split across lines — an exact-same-line key or a fixed line-window would
            // miss them; the ancestor-walk consumer resolution does not. The deliberately weird layouts
            // are fenced off from the formatter so they survive verbatim. ---
            // csharpier-ignore-start

            // C1 the AgedState.RegisterTermEndProcess shape: delegate on its own line below `new`.
            new BackgroundProcessSchedule(
                DateTime.Now,
                EndOfTerm,
                "end-of-term"
            );

            // C2 the `new`, the type, and arg #1 each split across lines, pushing the delegate many
            // lines past the `new` keyword — a fixed line-window from the ctor would never reach it.
            new
                RepeatingBackgroundProcessSchedule(
                    TimeSpan
                        .FromMinutes(
                            10
                        ),
                    ReindexDirectory,
                    "reindex"
                );

            // C3 member-access method-group (`this.X`) as the delegate, with the access split too.
            new BackgroundProcessSchedule(DateTime.Now,
                this
                    .SweepCache, "sweep");

            // C4 dispatcher ctor NESTED inside another call's argument list — the delegate's consumer is
            // the INNER `new` (nearest enclosing creation), not the outer Register call.
            Register(
                new BackgroundProcessSchedule(
                    DateTime.Now, FlushAudit,
                    "flush"
                )
            );

            // C5 comment lines interleaved between `new` and the delegate (the exact gap that defeats a
            // line-window heuristic; the structural walk is unaffected).
            new BackgroundProcessSchedule(
                // run the nightly purge once the grace period elapses
                DateTime.Now,
                // the real work:
                PurgeNightly,
                "purge-nightly"
            );

            // C6 RECALL GUARD: a synchronous method-group handed to a NON-dispatcher helper sitting right
            // among the dispatcher registrations must STAY a synchronous call (its consumer is RunNow,
            // not a dispatcher) — never swept into a handoff.
            RunNow(
                SyncTransform
            );

            // csharpier-ignore-end
        }

        // --- Callbacks (execution origins). Each carries a real effect. ---

        public void ProcessHealthcodeQueue()
        {
            var invoice = new InvoiceEntity { InvoiceId = 1 };
            _db.SaveEntity(invoice);
            _soap.SubmitBill("<bill/>");
        }

        public void CleanupExpired()
        {
            var patient = new PatientEntity { PatientId = 9 };
            _db.FetchEntity(patient);
        }

        public void OnChamberChanged(string chamber)
        {
            var patient = new PatientEntity { PatientId = 2 };
            _db.FetchEntity(patient);
        }

        public void HandleMessage(string message)
        {
            var invoice = new InvoiceEntity { InvoiceId = 3 };
            _db.SaveEntity(invoice);
        }

        public void BackgroundWork()
        {
            var patient = new PatientEntity { PatientId = 7 };
            _db.FetchEntity(patient);
        }

        // --- Callbacks for the convoluted multi-line registrations (C1–C6). Each carries a real
        // effect, so a handoff classification is observable: sync-cut must NOT reach it, --async must. ---

        public void EndOfTerm()
        {
            var invoice = new InvoiceEntity { InvoiceId = 11 };
            _db.SaveEntity(invoice);
        }

        public void ReindexDirectory()
        {
            var patient = new PatientEntity { PatientId = 12 };
            _db.FetchEntity(patient);
        }

        public void SweepCache()
        {
            var patient = new PatientEntity { PatientId = 13 };
            _db.FetchEntity(patient);
        }

        public void FlushAudit()
        {
            var invoice = new InvoiceEntity { InvoiceId = 14 };
            _db.SaveEntity(invoice);
        }

        public void PurgeNightly()
        {
            var invoice = new InvoiceEntity { InvoiceId = 15 };
            _db.SaveEntity(invoice);
        }

        // The recall-guard negative (C6): handed to a non-dispatcher, so it is an ordinary synchronous
        // call — reachable from RegisterSchedules under sync-cut, never classified as a handoff.
        public void SyncTransform()
        {
            var patient = new PatientEntity { PatientId = 16 };
            _db.FetchEntity(patient);
        }

        // Non-dispatcher consumers used by the convoluted layouts. Register just stores the schedule;
        // RunNow invokes its delegate synchronously. Neither matches a handoffDispatchers rule.
        private void Register(BackgroundProcessSchedule schedule) { }

        private void RunNow(BackgroundProcessScheduleDelegate work) => work();
    }
}
