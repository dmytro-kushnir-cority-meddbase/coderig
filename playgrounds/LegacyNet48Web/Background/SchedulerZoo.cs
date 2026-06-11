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
    }
}
