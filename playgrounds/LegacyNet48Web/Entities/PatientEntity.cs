using System;
using SD.LLBLGen.Pro.ORMSupportClasses;
using MedDBase.Messages;

namespace LegacyNet48Web.Entities
{
    public class PatientEntity : EntityBase2
    {
        public int PatientId { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public Guid TrackingGuid { get; set; }
    }

    public class PatientCollection : EntityCollectionBase2<PatientEntity> { }

    public class PatientSyncMsg : IChamberMsg
    {
        public Guid ChamberGuid { get; set; }
        public int PatientId { get; set; }
    }
}
