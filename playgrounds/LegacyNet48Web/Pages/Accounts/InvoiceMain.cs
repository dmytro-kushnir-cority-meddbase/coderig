using MMS.Web.UI.Attributes;
using LegacyNet48Web.Pages.Proxies;

namespace LegacyNet48Web.Pages.Accounts
{
    // A real ClientPage (via InvoiceMainBase -> ClientPage, two hops).
    // Its [ClientAction] methods are genuine action entry points.
    public class InvoiceMain : InvoiceMainBase
    {
        public InvoiceMain() { }

        [ClientAction]
        public void SubmitInvoice()
        {
            // Navigating to another page via a generated proxy IS a clientpage_proxy effect
            // (declaring type InvoiceEditProxy derives ProxyBase).
            var proxy = new InvoiceEditProxy();
            proxy.ShowDialog("dlg", "Edit invoice");

            // A class whose NAME ends in "Proxy" but does NOT derive ProxyBase is NOT a navigation
            // proxy — its ShowDialog must NOT be a clientpage_proxy effect (suffix is not the gate).
            var notAProxy = new InvoiceServiceProxy();
            notAProxy.ShowDialog("x", "y");
        }

        [ClientAction]
        public void RefreshList()
        {
        }

        // No [ClientAction]: a plain helper, NOT an action entry point.
        public void RecomputeTotals()
        {
        }
    }
}
