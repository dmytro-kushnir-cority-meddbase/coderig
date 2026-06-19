using Contracts;
using Foundation; // Foundation is referenced only TRANSITIVELY (via Contracts) — DataAccess uses

// Db.Query without a direct ProjectReference. Compiles because project references flow
// transitively at build; rig must pull Foundation into the live in-set closure so the
// call edge to Db.Query binds to ONE assembly identity rather than dropping.

namespace DataAccess;

public sealed class PatientRepository : IPatientRepository
{
    public PatientDto? GetById(int id)
    {
        // The DB effect at the base of the chain — the reachability target.
        var raw = Db.Query($"SELECT Name FROM Patients WHERE Id = {id}");
        return new PatientDto { Id = id, Name = raw };
    }
}
