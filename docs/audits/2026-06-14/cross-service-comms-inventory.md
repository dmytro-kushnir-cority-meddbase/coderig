# MedDBase cross-service / external communication inventory (besides Echo)

Echo.Process (over a Redis backplane) is the internal actor bus. Everything below is the rest, all
confirmed in source under `C:\Git\meddbase-main-application\src`.

## Summary
Beyond Echo, MedDBase uses: **HTTP/REST** (`HttpClient`, `WebClient`, `Flurl`) for many external integrations and internal service-to-service calls; **SOAP over HTTP** (legacy `SoapHttpClientProtocol` Web References) for Healthcode and Lab integrations; **WCF REST services** (server-side `[ServiceContract]` + `WebServiceHostFactory`) consumed internally over plain HTTP; **raw TCP sockets** for PACS HL7 MLLP; **UDP** for diagnostics/syslog; **SMTP/POP3** for email; **Azure EventGrid** for audit events; **cloud blob SDKs** (S3/Azure/GCS) for DFS; **FHIR over HTTPS** for NHS; **outbound webhooks**; and the **ObjectStore** (SQL table) as DB-as-queue. No MSMQ, RabbitMQ, gRPC, SignalR-client, or WCF-client.

## Inventory
| Mechanism | Library / Namespace | Int/Ext | Where (file) | Purpose |
|---|---|---|---|---|
| Echo (Redis backplane) | `Echo.Process`, `StackExchange.Redis` | Internal | `echo-process/Echo.Process.Redis/RedisClusterImpl.cs` | Actor dispatch across nodes |
| HTTP — HttpClient | `System.Net.Http.HttpClient` | Both | `SMS/SMSService.cs:416`, `AddressFinder/IdealPostcodesLookupService.cs:75`, `DocShareApiClient_Generated.cs:192` | SMS (CM.com), postcode lookup, internal DocShare API, OIDC, Patient Portal proxy |
| HTTP — WebClient | `System.Net.WebClient` | Both | `PhysioTec/PhysioTecApi.cs:71`, `SnomedProxy/ProxyBase.aspx.cs:27`, `PrintService/PrintService.cs:483` | PhysioTec, SNOMED proxy, PDF/print |
| HTTP — Flurl | `Flurl.Http` | Both | `MedicareAu/.../AccessRequests.cs:14`, `SignatureRx/SignatureRxHttp.cs`, `OpayoPi/OpayoPiHttp.cs` | Medicare PRODA + verification, SignatureRx e-Rx, Opayo payments |
| SOAP/HTTP (client) | `System.Web.Services.Protocols.SoapHttpClientProtocol` | Both | `Web References/HealthcodeWebServices/Reference.cs` (used `Master_HealthcodeServiceImpl.cs:1050`); `Web References/LabsServer/Reference.cs`; `lab/Labs.Logic/Mirth.SoapProxy.cs` | Healthcode claims; internal LabsServer; Mirth lab channels |
| WCF REST (server only) | `System.ServiceModel`, `[ServiceContract]`, `WebServiceHostFactory` | Internal | `pdf/PdfService/PdfService.cs:28`; `Integrations/HL7.cs:32`; `Diagnostics/Monitoring.cs:30` | PdfService / HL7 ingest / diagnostics endpoints, called over plain HTTP |
| Raw TCP (HL7 MLLP) | `System.Net.Sockets.TcpListener`/`TcpClient` | External | `pacs/MedDBase.PACS/Inbound/InboundServer.cs:22`; `Outbound/OutboundMessageQueue.cs` | PACS receives/sends HL7 to imaging equipment |
| UDP | `System.Net.Sockets.UdpClient` | Both | `Nucleus/Services/UdpService.cs`; `GpConnect/Logging/Syslog/SyslogLogger.cs:52` | Diagnostics; GP Connect syslog |
| SMTP (send) | `MailKit.Net.Smtp.SmtpClient` | External | `email/src/Smtp.cs:18`; `MailSendReceive/Services/SendService.cs` | MailSendReceive outbound email |
| POP3 (receive) | custom `TcpClientBase`/`Pop3` | External | `email/src/Pop3.cs:12`; `MailSendReceive/Services/ReceiveService.cs` | MailSendReceive inbound email |
| Azure EventGrid | `Azure.Messaging.EventGrid` | External | `mms/MMS/Audits/EventGridAuditLog.cs:12`; `MainAppAuditLog.cs:54`; `LogProcess.cs:46` | Publishes audit events to EventGrid topic |
| ObjectStore (DB-IPC) | SQL via LLBLGen (`OBJECT_STORE`) | Internal | `Healthcode/Master_HealthcodeServiceImpl.cs:251`; `Application.Core/ObjectStore.cs` | Workflow state machine serialised to SQL, polled by background masters — DB-as-queue |
| AWS S3 | `Amazon.S3.AmazonS3Client` | External | `dfs/src/Live/AWSS3Client.cs:31` | DFS v5 document store |
| Azure Blob | `Azure.Storage.Blobs` | External | `dfs/src/Live/AzureBlobStorageClient.cs` | DFS v4/3 document store |
| Google Cloud Storage | GCS client | External | `dfs/src/Live/GoogleCloudStorageClient.cs` | DFS v6 document store |
| WebSocket (Echo ProcessSys) | `System.Net.WebSockets` | Internal | `echo-process/Echo.Process.Owin/ProcessSysWebSocket.cs`; `scanning/MedDBase.Scan.Site/ProcessSys/ProcessSysListener.cs` | Browser↔actor bridge |
| FHIR over HTTPS (PDS) | `Hl7.Fhir.Rest.FhirClient` | External | `nhs-pds/MedDBase.Nhs.Pds/Client/ApiClient.cs:25` | NHS PDS demographic queries |
| FHIR over HTTPS (GP Connect) | `Hl7.Fhir.Rest.FhirClient` | External | `nhs-gpconnect/MedDBase.GpConnect/Services/GPConnectService.cs` | GP Connect via Spine Security Proxy (mTLS) |
| Xero OAuth2/API | `HttpClient` | External | `external-auth/MedDBase.ExternalAuth.Xero/IO/XeroConnectionManagerEnv.cs:35` | Xero accounting |
| OIDC | `IdentityModel.OidcClient` + `HttpClient` | External | `external-auth/MedDBase.ExternalAuth.Common/Oidc.cs:46` | SMTP OAuth2 / OIDC login |
| Webhooks (outbound POST) | `HttpClient` (`IWebhookHttpClient`) | External | `webhooks/MedDBase.Webhooks/Publisher.cs:86`; `WebhookHttpClient.cs` | POST event payloads to customer URLs |
| Syslog (TCP/UDP) | custom `SyslogTcpSender` + `Syslog.Framework.Logging` | External | `nhs-gpconnect/.../Syslog/SyslogTcpSender.cs:7`; `SyslogLogger.cs:52` | NHS GP Connect compliance syslog |
| Lab SOAP (internal) | `SoapHttpClientProtocol` | Internal | `login/MedDBase.Login/WebServices/OrderResponses.asmx.cs:167`; `Web References/LabsServer/Reference.cs` | Main app ↔ internal LabsServer ASMX |

## External integrations — transport
| Integration | Transport |
|---|---|
| Healthcode (UK claims) | SOAP/HTTP (`submitBill`, `billStatus`, …) to `services.healthcode.co.uk` |
| Medicare AU (PRODA + verify) | REST/JSON via Flurl; PRODA JWT-bearer; verification POST JSON |
| NHS PDS | FHIR R4 over HTTPS |
| NHS GP Connect | FHIR over HTTPS via SSP (mTLS + JWT) |
| Opayo (SagePay) | REST/JSON via Flurl |
| PciPal (IVR payment) | HTTP + webhook callback |
| SignatureRx (e-Rx) | REST/JSON via Flurl (OAuth2 first) |
| CM.com (SMS) | HTTP POST XML to `gw.cmtelecom.com` |
| Mirth Connect (labs) | SOAP/HTTP per-channel (+ file-drop) |
| PhysioTec | REST/JSON via WebClient |
| Google Maps geocoding | HTTPS via WebRequest |
| Ideal Postcodes | HTTPS via HttpClient |
| Xero | HTTPS via HttpClient |
| PACS/RIS equipment | TCP raw sockets (HL7 MLLP) |
| Azure EventGrid | HTTPS (SDK) |
| Document storage (DFS) | AWS S3 / Azure Blob / GCS SDK (version-switched) |

## Confirmed absences (searched, zero hits in `src/`)
gRPC (`Grpc.*`, `.proto`); MSMQ (`System.Messaging`); RabbitMQ / MassTransit / NServiceBus; Azure Service Bus / Event Hubs; Kafka; **WCF client-side** (`ChannelFactory`, `ClientBase<T>`); SignalR (`Microsoft.AspNet.SignalR`, `Hub`); named pipes (`NamedPipe*Stream`); RestSharp / Refit; FTP/SFTP (`FtpWebRequest`, `Renci.SshNet`); AWS SQS/SNS.

## Notes for a human
- WCF is used only as a server-side hosting shim (`WebServiceHostFactory`), not the channel/binding client model.
- `Chamber.ObjectStore` is DB-as-queue (cross-process via shared SQL, not a broker) — confirm whether it counts as "cross-service" for your purposes.
- `MessageStream/Connection.cs` (TCP "PostBox" client) and `UdpService` appear to have no active callers — likely dead code; verify against deployment config.
- PACS HL7 has a dual path: raw TCP (MLLP) and an HTTP REST ingest endpoint, both feeding the same pipeline.
