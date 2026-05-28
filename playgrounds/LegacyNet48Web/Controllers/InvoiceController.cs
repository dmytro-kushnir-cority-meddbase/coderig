using System.Threading.Tasks;
using System.Web.Http;
using LegacyNet48Web.Entities;

namespace LegacyNet48Web.Controllers
{
    [RoutePrefix("api/invoices")]
    public class InvoiceController : ApiController
    {
        private readonly DataAdapter _db;

        public InvoiceController(DataAdapter db) { _db = db; }

        [HttpGet, Route("")]
        public async Task<IHttpActionResult> GetAll()
        {
            var invoices = new InvoiceCollection();
            await _db.GetMultiAsync(invoices, null!);
            return new IHttpActionResult();
        }

        [HttpPost, Route("{id:int}/approve")]
        public IHttpActionResult Approve(int id)
        {
            var invoice = new InvoiceEntity { InvoiceId = id };
            invoice.Status = "Approved";
            invoice.Save();
            return new IHttpActionResult();
        }
    }
}
