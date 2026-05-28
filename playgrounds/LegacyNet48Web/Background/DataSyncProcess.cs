using MedDBase.Application.Core.Background;
using LegacyNet48Web.Entities;
using MedDBase.Messages;

namespace LegacyNet48Web.Background
{
    public class DataSyncProcess : IBackgroundProcess
    {
        private readonly DataAdapter _db;

        public DataSyncProcess(DataAdapter db) { _db = db; }

        public void Process()
        {
            var patient = new PatientEntity { PatientId = 1 };
            _db.FetchEntity(patient);

            var msg = new PatientSyncMsg { ChamberGuid = patient.TrackingGuid };
            msg.tell();
        }
    }
}
