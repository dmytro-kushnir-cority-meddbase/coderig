using MMS.Web.UI.Attributes;

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
    }
}
