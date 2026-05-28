using SD.LLBLGen.Pro.ORMSupportClasses;

namespace LegacyNet48Web.Entities
{
    public class InvoiceEntity : EntityBase2
    {
        public int InvoiceId { get; set; }
        public string? Status { get; set; }
        public decimal Amount { get; set; }
    }

    public class InvoiceCollection : EntityCollectionBase2<InvoiceEntity> { }
}
