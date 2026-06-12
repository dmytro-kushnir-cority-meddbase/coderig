namespace LegacyNet48Web.Pages.Proxies
{
    // Base of every generated navigation proxy. Mirrors MedDBase.Pages.ProxyBase: it carries the
    // shared plumbing; the concrete <Page>Proxy classes declare the public nav methods.
    public abstract class ProxyBase
    {
        protected void LoadChildDialog(string id, string title) { }
    }

    // Stands in for the SOURCE-GENERATED InvoiceEdit_RequestProxy: a `<Page>Proxy : ProxyBase` that
    // DECLARES its own Show/ShowDialog/Redirect (exactly as RequestResponseProxyGenerator emits).
    // A call to any of these is a clientpage_proxy navigation effect — detected by the declaring
    // type deriving ProxyBase, NOT by the "Proxy" name suffix.
    public class InvoiceEditProxy : ProxyBase
    {
        public void Show(string containerId, string pageId) { }

        public bool ShowDialog(string dialogContainerId, string dialogTitle) => false;

        public void Redirect(string url) { }
    }
}
