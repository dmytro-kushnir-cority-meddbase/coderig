namespace LegacyNet48Web.Pages.Accounts
{
    // A hand-written class whose name ends in "Proxy" but which is NOT a generated navigation
    // proxy (it does not derive MedDBase-style ProxyBase). Its ShowDialog must NOT be detected as
    // a clientpage_proxy effect — proving the base-type gate, not the "Proxy" name suffix, is the
    // discriminator.
    public class InvoiceServiceProxy
    {
        public bool ShowDialog(string a, string b) => false;
    }
}
