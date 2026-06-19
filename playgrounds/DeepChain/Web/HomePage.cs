namespace Web;

// Top of the chain — the entry point. Show() reaches the bottom DB effect (Foundation.Db.Query) five
// project hops down: Web -> ApiGateway -> Business -> (dispatch) DataAccess -> Foundation. Both
// ApiGateway.BookingController and Contracts.PatientDto are used here only via transitive references.
public sealed class HomePage
{
    private readonly ApiGateway.BookingController _controller;

    public HomePage(ApiGateway.BookingController controller) => _controller = controller;

    public string Show() => _controller.Book(new Contracts.PatientDto { Id = 42, Name = "Ada" });
}
