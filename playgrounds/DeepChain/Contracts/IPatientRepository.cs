namespace Contracts;

public interface IPatientRepository
{
    PatientDto? GetById(int id);
}
