using System.Web.Http;
using LegacyNet48Web.Entities;

namespace LegacyNet48Web.Controllers
{
    [RoutePrefix("api/patients")]
    public class PatientController : ApiController
    {
        private readonly DataAdapter _db;

        public PatientController(DataAdapter db) { _db = db; }

        [HttpGet, Route("")]
        public IHttpActionResult GetAll()
        {
            var patients = new PatientCollection();
            _db.GetMulti(patients, null!);
            return new IHttpActionResult();
        }

        [HttpGet, Route("{id:int}")]
        public IHttpActionResult GetById(int id)
        {
            var patient = new PatientEntity();
            patient.PatientId = id;
            patient.Fetch();
            return new IHttpActionResult();
        }

        [HttpPost, Route("")]
        public IHttpActionResult Create([FromBody] PatientEntity patient)
        {
            _db.StartTransaction(System.Data.IsolationLevel.ReadCommitted, "CreatePatient");
            patient.Save();
            _db.Commit();
            return new IHttpActionResult();
        }

        [HttpPut, Route("{id:int}")]
        public IHttpActionResult Update(int id, [FromBody] PatientEntity patient)
        {
            patient.PatientId = id;
            patient.Save(true);
            return new IHttpActionResult();
        }

        [HttpDelete, Route("{id:int}")]
        public IHttpActionResult Delete(int id)
        {
            var patient = new PatientEntity { PatientId = id };
            patient.Delete();
            return new IHttpActionResult();
        }
    }
}
