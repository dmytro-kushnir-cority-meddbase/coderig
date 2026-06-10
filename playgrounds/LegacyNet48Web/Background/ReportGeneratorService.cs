using LegacyNet48Web.Entities;
using MedDBase.Nucleus.Interfaces.Services;

namespace LegacyNet48Web.Background
{
    public class ReportGeneratorService : ServiceBase
    {
        private readonly DataAdapter _db;

        public ReportGeneratorService(DataAdapter db)
        {
            _db = db;
        }

        public override void Startup()
        {
            var invoices = new InvoiceCollection();
            _db.GetMulti(invoices, null!);
        }
    }
}
