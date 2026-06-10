using SD.LLBLGen.Pro.ORMSupportClasses;

namespace LegacyNet48Web.Entities
{
    public class InvoiceEntity : EntityBase2
    {
        // Empty ctor: a NEW entity, not a fetch (object-initializer usages exercise this — they must
        // NOT be derived as reads). The pk / pk+transaction ctors ARE llblgen fetches (gap G5).
        public InvoiceEntity() { }

        public InvoiceEntity(int pkInvoice)
        {
            InvoiceId = pkInvoice;
        }

        public InvoiceEntity(int pkInvoice, ITransaction transaction)
        {
            InvoiceId = pkInvoice;
        }

        public int InvoiceId { get; set; }
        public string? Status { get; set; }
        public decimal Amount { get; set; }
    }

    public class InvoiceCollection : EntityCollectionBase2<InvoiceEntity> { }
}
