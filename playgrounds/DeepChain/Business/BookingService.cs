using Contracts;

namespace Business;

public sealed class BookingService : Domain.IBookingService
{
    private readonly IPatientRepository _repository;

    public BookingService(IPatientRepository repository) => _repository = repository;

    public string Book(int patientId)
    {
        // Interface dispatch: the static target is IPatientRepository.GetById; rig's dispatch edge
        // (impl) resolves it to DataAccess.PatientRepository.GetById, continuing the reach to Db.Query.
        var patient = _repository.GetById(patientId);
        return patient is null ? "no patient" : $"booked {patient.Name}";
    }
}
