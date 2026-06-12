using MMS.Web.UI;

namespace LegacyNet48Web.Pages.Accounts
{
    // Intermediate base so InvoiceMain reaches ClientPage in TWO hops.
    // Exercises the BFS closure (the gate must follow base -> base -> ClientPage).
    public abstract class InvoiceMainBase : ClientPage { }
}
