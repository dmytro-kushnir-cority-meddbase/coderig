using Domain;

namespace ApiGateway;

public sealed class BookingController
{
    private readonly IBookingService _bookings;

    public BookingController(IBookingService bookings) => _bookings = bookings;

    // The signature mentions Contracts.PatientDto, a type ApiGateway only reaches transitively (via
    // Business -> Domain/DataAccess -> Contracts). If the loader sees PatientDto as a second assembly
    // identity, this call's binding breaks and the edge into Business.BookingService.Book is dropped.
    public string Book(Contracts.PatientDto dto) => _bookings.Book(dto.Id);
}
