using MMS.Web.UI;

namespace LegacyNet48Web.Pages.Accounts
{
    // A ClientPage that happens to declare its OWN ShowDialog() — NOT a navigation proxy.
    // Mirrors the real MedDBase TermsAndConditions.ShowDialog false positive: the over-broad
    // clientpage_proxy fact rule (ShowDialog on any type under the Pages namespace, no "Proxy"
    // suffix gate) wrongly flags this as a page-navigation effect.
    public class TermsAndConditions : ClientPage
    {
        public bool ShowDialog() => true;

        protected override void OnLoad()
        {
            // Calling our own ShowDialog must NOT be a clientpage_proxy effect.
            ShowDialog();
        }
    }
}
