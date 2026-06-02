using MMS.Web.UI;
using MMS.Web.UI.Attributes;

namespace LegacyNet48Web.Pages.Workflows
{
    public interface IWorkflowMaster { }

    public sealed class ReferralMaster : IWorkflowMaster { }

    // GENERIC abstract base page. Mirrors the real MedDBase ConfigurationPaneBase<M> : ClientPage.
    // Concrete subclasses reach ClientPage through this generic base — the edge stored for the
    // subclass is the *instantiated* form (WorkflowPaneBase{ReferralMaster}) while this type's own
    // base edge is the *open* form (WorkflowPaneBase`1). The BFS closure must bridge the two.
    public abstract class WorkflowPaneBase<TMaster> : ClientPage
        where TMaster : IWorkflowMaster
    {
        // [ClientAction] on the generic base: inherited and dispatchable on every concrete pane,
        // so it is a real action entry point even though its declaring type is the abstract base.
        [ClientAction]
        public virtual void Save() { }
    }
}
