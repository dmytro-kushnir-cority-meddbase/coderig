using MMS.Web.UI;
using MMS.Web.UI.Attributes;

namespace LegacyNet48Web.Pages.Components
{
    // A component/widget under the Pages namespace that inherits ClientControl, NOT ClientPage.
    // Its [ClientAction] method must NOT be derived as a page action entry point — this is the
    // exact shape of the ~1083 over-matched actions in MedDBase (the missing ClientPage gate).
    public class InvoiceGrid : ClientControl
    {
        [ClientAction]
        public void Sort()
        {
        }

        [ClientAction]
        public void Page(int pageNumber)
        {
        }
    }
}
