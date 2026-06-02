using MMS.Web.UI.Attributes;
using LegacyNet48Web.Entities;

namespace LegacyNet48Web.Pages.Workflows
{
    // Concrete pane reaching ClientPage via the GENERIC base WorkflowPaneBase<ReferralMaster>.
    // Must be derived as a page entry point AND its [ClientAction] methods as action entry points —
    // the generic-base closure traversal is what makes this work.
    public class ReferralPane : WorkflowPaneBase<ReferralMaster>
    {
        public ReferralPane() { }

        [ClientAction]
        public void Submit() { }

        // Overrides the GENERIC base's virtual Save and writes to the DB. No [ClientAction] here —
        // the attribute lives on the base. This is the base-virtual -> override dispatch shape:
        // a call resolved to WorkflowPaneBase`1.Save must reach THIS override (and its effect).
        public override void Save()
        {
            new DataAdapter().SaveEntity(new InvoiceEntity());
        }
    }
}
