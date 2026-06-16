using Flurl;

namespace LegacyNet48Web.External
{
    // Stand-ins for the unmodeled external providers in gap G4: a generated SOAP web-service proxy,
    // an HTTP/PDF print client, a background-queue dispatcher, and an LLM client. Effects on these
    // are pure RULE DATA (provider/operation/methods/declaringTypes) — the deriver needs no code
    // change, only a rule per provider. Mirrors HCWebServices.submitBill, print services, queue
    // dispatch, and SmartLetter.GetSmartLetterResponse in MedDBase.

    // Generated SOAP proxy base (mirrors System.Web.Services.Protocols.SoapHttpClientProtocol).
    public abstract class SoapClientBase { }

    public sealed class HealthcodeServiceProxy : SoapClientBase
    {
        public string SubmitBill(string xml) => "ok";
    }

    public sealed class PdfPrintClient
    {
        public void RenderPdf(string document) { }
    }

    public sealed class QueueDispatcher
    {
        public void Enqueue(string message) { }
    }

    public sealed class SmartLetterClient
    {
        public string GetSmartLetterResponse(string prompt) => "letter";
    }

    // Per-service process-name table (mirrors MedDBase's ProcessDns / EchoConfig.ProcessNames).
    // static-readonly, NOT const — so const-resolution can't fold these; argument_name carries the path.
    public static class ProcessDns
    {
        public static readonly Echo.ProcessId AccountService = default;
        public static readonly string WorkerName = "worker";
    }

    // A caller that exercises every external provider, so the deriver sees the invocation facts.
    public sealed class OutboundGateway
    {
        public void SendEverything()
        {
            new HealthcodeServiceProxy().SubmitBill("<bill/>");
            new PdfPrintClient().RenderPdf("invoice");
            new QueueDispatcher().Enqueue("job");
            new SmartLetterClient().GetSmartLetterResponse("draft a letter");

            // Echo actor traffic (#16). The process NAME / routing target is the FIRST argument,
            // referenced through the ProcessDns table (a static-readonly ProcessId/ProcessName field —
            // NOT a compile-time const), so the actor:* rules resolve via argument_name to the member
            // path "ProcessDns.AccountService" rather than collapsing to the Echo.Process declaring type.
            Echo.Process.spawn<string>(ProcessDns.WorkerName, _ => { });
            Echo.Process.tell(ProcessDns.AccountService, "msg");
            Echo.Process.ask<string>(ProcessDns.AccountService, "query");
            // Implicit-target send: arg 0 is the MESSAGE, not a ProcessId — excluded from the tell rule
            // so it never mislabels the message expression as the routing target.
            Echo.Process.tellSelf("self-msg");

            // Flurl URL building (#16): the path-segment literal at arg 0 (the URL is the extension
            // receiver, not in the syntactic arg list) scopes the http effect to the route.
            "https://api.example.com".AppendPathSegment("submit");
        }
    }
}

// Minimal stand-in for the Flurl URL builder — the http:route rule matches purely on the declaring-type
// FQN, so the deriver needs no code change. (The Echo.Process actor prelude stub lives in MedDBaseStubs.cs.)
namespace Flurl
{
    public class Url
    {
        public Url AppendPathSegment(object segment, bool fullyEncode = false) => this;
    }

    public static class GeneratedExtensions
    {
        public static Url AppendPathSegment(this string url, object segment, bool fullyEncode = false) => new Url();
    }
}
