using SD.LLBLGen.Pro.ORMSupportClasses;

namespace LegacyNet48Web.Entities
{
    // Exercises the llblgen entity-constructor fetch (gap G5): `new XxxEntity(pk[, txn])` reads the
    // row, whereas `new XxxEntity()` (object initializer) constructs an empty entity and is NOT a read.
    public sealed class EntityFetcher
    {
        public void Load(int pkInvoice, ITransaction transaction)
        {
            var byPk = new InvoiceEntity(pkInvoice);                 // fetch (1 arg)
            var byPkTxn = new InvoiceEntity(pkInvoice, transaction); // fetch (2 args)
            var blank = new InvoiceEntity { InvoiceId = pkInvoice }; // NOT a fetch (empty ctor)
        }
    }
}
