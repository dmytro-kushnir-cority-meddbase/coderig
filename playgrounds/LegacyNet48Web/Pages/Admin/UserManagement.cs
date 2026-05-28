using MMS.Web.UI;
using LegacyNet48Web.Entities;

namespace LegacyNet48Web.Pages.Admin
{
    public class UserManagement : ClientPage
    {
        private readonly DataAdapter _db;

        public UserManagement(DataAdapter db) { _db = db; }

        protected override void OnLoad()
        {
            var patients = new PatientCollection();
            _db.GetMulti(patients, null!);
        }
    }
}
