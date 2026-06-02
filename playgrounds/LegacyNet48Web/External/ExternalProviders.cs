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

    // A caller that exercises every external provider, so the deriver sees the invocation facts.
    public sealed class OutboundGateway
    {
        public void SendEverything()
        {
            new HealthcodeServiceProxy().SubmitBill("<bill/>");
            new PdfPrintClient().RenderPdf("invoice");
            new QueueDispatcher().Enqueue("job");
            new SmartLetterClient().GetSmartLetterResponse("draft a letter");
        }
    }
}
