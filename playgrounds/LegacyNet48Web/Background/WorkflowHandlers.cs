using MedDBase.Application.Workflows;
using MedDBase.Wcf;
using LegacyNet48Web.Entities;

namespace LegacyNet48Web.Background
{
    // requireOverride shape: only OnSave (an override of the framework virtual) is an entry point.
    // OnCancel is inherited-but-not-overridden here, and HelperSave is a plain method that happens
    // to match a handler name but is NOT an override — neither must be derived.
    public class InvoiceWorkflowController : WorkflowControllerBase
    {
        private readonly DataAdapter _db;

        public InvoiceWorkflowController(DataAdapter db) { _db = db; }

        public override void OnSave()
        {
            var invoice = new InvoiceEntity { InvoiceId = 1 };
            _db.SaveEntity(invoice);
        }

        public void HelperSave()
        {
            var invoice = new InvoiceEntity { InvoiceId = 2 };
            _db.SaveEntity(invoice);
        }
    }

    // WCF shape: methods carrying [OperationContract] are entry points regardless of base type
    // (the baseTypes:["*"] rule, narrowed by the attribute). SubmitClaim qualifies; Helper does not.
    public class ClaimsService
    {
        private readonly DataAdapter _db;

        public ClaimsService(DataAdapter db) { _db = db; }

        [OperationContract]
        public void SubmitClaim()
        {
            var invoice = new InvoiceEntity { InvoiceId = 3 };
            _db.SaveEntity(invoice);
        }

        public void Helper()
        {
        }
    }
}
