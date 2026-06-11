namespace EntryPointEffects.Api.Services;

// B1 fixture — mirrors the MedDBase Echo gateway shape: a static ask<TResponse>(target, msg) / tell(
// target, msg) where the response type is a generic TYPE ARGUMENT and the routing target is a member
// access (a ProcessId DNS constant). Exercises call-site capture of TypeArguments + FirstArgumentName
// through a REAL index → derive (not just the in-memory extractor).

public sealed class PaymentGatewayResponse<T> { }

public static class PaymentGatewayProcessDns
{
    public static int AccountService => 1;

    public static int PaymentService => 2;
}

public static class PaymentGatewayProcess
{
    public static T Ask<T>(int target, object msg) => default!;

    public static void Tell(int target, object msg) { }
}

public sealed class PaymentGatewayCaller
{
    public void Dispatch(object msg)
    {
        // ask<PaymentGatewayResponse<Team>>(AccountService, msg): the asked type is a generic type
        // argument; the target is the member path PaymentGatewayProcessDns.AccountService.
        PaymentGatewayProcess.Ask<PaymentGatewayResponse<Data.Team>>(PaymentGatewayProcessDns.AccountService, msg);

        // tell(PaymentService, msg): non-generic; the target member path is the captured resource.
        PaymentGatewayProcess.Tell(PaymentGatewayProcessDns.PaymentService, msg);
    }
}
