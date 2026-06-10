using LegacyNet48Web.Entities;
using MMS.Web.UI;

namespace LegacyNet48Web.Pages.Account.Public
{
    // A reflection-loaded PageBase page (gap G2) — the legacy login path. PageLoad.Create()
    // instantiates it and calls Initialise/OnAction, so the page is a navigable entry point AND
    // its lifecycle hooks are handler entry points (the effects in OnAction would otherwise be
    // unreachable, since nothing calls OnAction from the constructor).
    public class LegacyLogin : PageBase
    {
        public LegacyLogin() { }

        public override void Initialise() { }

        public override void OnAction()
        {
            new DataAdapter().SaveEntity(new InvoiceEntity { InvoiceId = 1 });
        }
    }
}
