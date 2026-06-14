# MedDBase Third-Party Library I/O Detector Triage
**Date:** 2026-06-14  
**Analyst:** Claude (automated harvest + manual triage)  
**Purpose:** Enumerate every unique third-party library in the MedDBase solution and triage as effect-detector candidates for the `rig` code-intelligence tool.

---

## Method

### Manifests harvested

| Manifest | Location | Notes |
|---|---|---|
| `paket.dependencies` | repo root | 316 direct NuGet refs (plus 3 group blocks) — **authoritative direct deps** |
| `paket.lock` | repo root | **549 unique resolved packages** — authoritative full set |
| `packages.config` (×17) | scattered legacy projects | All 17 files inspected; all packages already covered by paket.lock |
| `Directory.Packages.props` | n/a | Not present — Paket is sole package manager |
| SDK-style `<PackageReference>` | n/a | Paket manages all NuGet; no standalone `<PackageReference>` outside paket |
| `package.json` / npm | repo root | Frontend JS — **not assessed** (not a rig target today) |

### Counts
- **Total unique packages (paket.lock):** 549  
- **Direct dependencies (paket.dependencies):** ~230 (across 4 groups)  
- **Transitive-only:** ~319  
- **Packages.config files:** 17 (all packages already in paket.lock)  
- **npm packages:** not enumerated (out of scope for .NET effect detectors)

### Method notes
- Paket lock is the authoritative resolved set; packages.config entries are a strict subset.  
- For each I/O candidate, at least one `Grep` hit in `src/**/*.cs` is cited to confirm real call-site usage ("referenced ≠ used").  
- Libraries with zero real call-site hits are noted as **referenced but apparently unused**.

---

## Detector candidates

> Sorted by Priority (High → Low) then Coverage (Gap → Partial → Covered).

| Library | Category | I/O? | Priority | rig coverage | Suggested rule |
|---|---|---|---|---|---|
| **Dapper** | DB/ORM (micro-ORM) | Yes | **High** | **Gap** | `provider:"dapper"` `operation:"query"` `methods:["Query","QueryAsync","QueryFirst","QueryFirstAsync","QuerySingle","QuerySingleAsync","QueryMultiple","QueryMultipleAsync"]` `declaringTypes:["Dapper.SqlMapper"]` `resource:"declaring_type"` — extension methods on `IDbConnection`; `resource:"declaring_type"` needed (same pattern as Flurl). Also `operation:"execute"` on `["Execute","ExecuteAsync","ExecuteScalar","ExecuteScalarAsync"]`. Confirmed call sites: `AuditsRepository.cs` (stored-proc execute + query), `ObservationRequestHelpers.cs`. |
| **Google.Cloud.PubSub.V1** | Message broker/queue | Yes | **High** | **Gap** | `provider:"gcp_pubsub"` `operation:"publish"` `methods:["PublishAsync","Publish"]` `receiverTypes:["Google.Cloud.PubSub.V1.PublisherClient"]` `resource:"receiver_type"`. Also `operation:"subscribe"` on `SubscriberClient.StartAsync`. Confirmed: `GcpPubSubAuditLog.cs` calls `publisherClient.PublishAsync`. |
| **Twilio** | SMS/telephony | Yes | **High** | **Gap** | `provider:"twilio"` `operation:"sms"` `methods:["CreateAsync","Create"]` `receiverTypes:["Twilio.Rest.Api.V2010.Account.MessageResource"]` `resource:"receiver_type"`. Also video/calls via `Twilio.Rest.Video.*` if used. Confirmed: `TwilioDriver.cs`, `TwilioAPIWrapper.cs`, 8 files in `src/sms/`. |
| **SendGrid** | Email (transactional HTTP) | Yes | **High** | **Gap** | `provider:"sendgrid"` `operation:"send"` `methods:["SendEmailAsync"]` `receiverTypes:["SendGrid.ISendGridClient","SendGrid.SendGridClient"]` `resource:"receiver_type"`. Confirmed: `MailMergeService.cs`, `MailMerge.cs`, `EditMailMergeInstance.cs`. |
| **RestSharp** | HTTP/REST client | Yes | **High** | **Gap** | `provider:"http"` `operation:"request"` `methods:["ExecuteAsync","Execute","GetAsync","PostAsync","PutAsync","DeleteAsync","PatchAsync"]` `receiverTypes:["RestSharp.IRestClient","RestSharp.RestClient"]` `resource:"receiver_type"`. Confirmed: `TwilioAPIWrapper.cs` (1 file; limited surface but it IS used). |
| **Xero.NetStandard.OAuth2** | HTTP/REST client (accounting API) | Yes | **High** | **Gap** | `provider:"xero"` `operation:"request"` — Xero SDK wraps HttpClient internally; the app-visible API is via generated `AccountingApi` methods like `GetAccountsAsync`, `CreateInvoicesAsync`. Gate on `receiverTypes:["Xero.NetStandard.OAuth2.Api.AccountingApi","Xero.NetStandard.OAuth2.Api.IAccountingApi"]`. Confirmed: 28 files in `src/external-auth/` and `src/main/MedDBase.ServiceTier/Accountancy/`. |
| **OpenAI** | HTTP/REST client (AI) | Yes | **High** | **Gap** | `provider:"openai"` `operation:"complete"` `methods:["CompleteChatAsync","CompleteChat"]` `receiverTypes:["OpenAI.Chat.ChatClient"]` `resource:"receiver_type"`. Also `operation:"embed"` on `EmbeddingClient`. Confirmed: `OpenAi.cs` in `src/main/MedDBase.ServiceTier/AI/`. |
| **GoogleApi** | HTTP/REST client (Maps/Places) | Yes | **High** | **Gap** | `provider:"google_maps"` `operation:"query"` `methods:["QueryAsync","Query"]` — GoogleApi uses a single generic `QueryAsync<TRequest,TResponse>` interface. Gate on `declaringTypes:["GoogleApi.GoogleMaps.*"]` or `receiverTypes` containing `IGoogleApi`. `resource:"declaring_type"`. Confirmed: `GoogleFuzzyAddressLookupService.cs`. |
| **Azure.Messaging.EventGrid** | Cloud event bus | Yes | **High** | **Partial** | Already in `rig.rules.json` as `eventbus:publish` on `EventGridPublisherClient.SendEventAsync`. **Gap:** missing `SendEventsAsync` (batch overload). Add `"SendEventsAsync"` to the existing methods array. |
| **System.DirectoryServices / .Protocols** | LDAP/AD directory | Yes | **High** | **Gap** | `provider:"ldap"` `operation:"search"` on `DirectorySearcher.FindAll/FindOne` (`receiverTypes:["System.DirectoryServices.DirectorySearcher"]`) and `LdapConnection.SendRequest` / `LdapConnection.BeginSendRequest` (`receiverTypes:["System.DirectoryServices.Protocols.LdapConnection"]`). Confirmed: `SpineDirectoryService.cs` (NHS GP Connect uses LDAP to query the Spine Directory Service). |
| **MimeKit / MailKit (IMAP/POP3)** | Email (IMAP receive) | Yes | **Med** | **Partial** | MailKit SMTP is **Covered** (builtin-rules.json). **Gap:** `ImapClient` and `Pop3Client` for *reading* mail — `Connect/ConnectAsync`, `GetMessage/GetMessageAsync`. `receiverTypes:["MailKit.Net.Imap.ImapClient","MailKit.Net.Pop3.Pop3Client"]`. MedDBase has a `MailSendReceive` background process — confirm if IMAP is active there. |
| **linq2db (LinqToDB)** | DB/ORM | Yes | **Med** | **Gap** | `provider:"linq2db"` `operation:"query"` `methods:["ToListAsync","FirstAsync","FirstOrDefaultAsync","SingleAsync","InsertAsync","UpdateAsync","DeleteAsync","ExecuteAsync"]` `receiverTypes:["LinqToDB.DataConnection","LinqToDB.Data.DataConnection"]` or via `IDataContext`. Confirmed: `src/webdav/` (4 files using `DataConnection`). |
| **Mindbox.Data.Linq** | DB/ORM (LINQ to SQL fork) | Yes | **Med** | **Gap** | `provider:"linq2sql"` — Mindbox is a LINQ-to-SQL fork. `methods:["SubmitChanges","ExecuteQuery","ExecuteCommand"]` `receiverTypes:["System.Data.Linq.DataContext"]`. Usage confirmed only in `MMS.Data.Linq` assembly-info files; **likely referenced but not actively called** — verify before adding rule. |
| **Azure.Messaging.ServiceBus** | Message broker/queue | Yes | **Med** | **Gap** | `provider:"azure_servicebus"` `operation:"send"` `methods:["SendMessageAsync","SendMessagesAsync"]` `receiverTypes:["Azure.Messaging.ServiceBus.ServiceBusSender"]`; `operation:"receive"` on `ReceiveMessageAsync`/`ReceiveMessagesAsync` on `ServiceBusReceiver`. **No call sites found** in src/ — package present but apparently unused in app code. Flag as low-value until confirmed used. |
| **Microsoft.Azure.ServiceBus** | Message broker/queue (legacy) | Yes | **Med** | **Gap** | Same as above — older SDK. `methods:["SendAsync","CompleteAsync"]` on `IQueueClient`/`ITopicClient`. **No call sites found.** Possibly a transitive dep of another package. |
| **IronPdf** | PDF/document | Yes | **Med** | **Gap** | `provider:"ironpdf"` `operation:"render"` `methods:["RenderHtmlAsPdf","RenderHtmlAsPdfAsync","FromHtml","FromUrl"]` `declaringTypes:["IronPdf.ChromePdfRenderer","IronPdf.PdfDocument"]` `resource:"declaring_type"`. Confirmed: `PdfService2/Converters/IronPdfConverter.cs`. IronPdf spawns a headless Chrome process — cross-process I/O. |
| **CefSharp.OffScreen** | Browser/rendering | Yes | **Med** | **Gap** | `provider:"browser_render"` `operation:"navigate"` `methods:["LoadUrlAsync","WaitForInitialLoadAsync","EvaluateScriptAsync"]` `receiverTypes:["CefSharp.OffScreen.ChromiumWebBrowser"]`. Confirmed: `PdfService.CefContainer/CefConverter.cs`. Spawns Chromium subprocess — cross-process I/O. |
| **ParquetSharp** | File I/O (columnar) | Yes | **Med** | **Gap** | `provider:"parquet"` `operation:"write"` `methods:["Close","Dispose"]` on `ParquetFileWriter`; `operation:"read"` on `ParquetFileReader.OpenRowGroup`. Confirmed: `TableParquetExtensions.cs` in client-data-transformation tools. File-system I/O. |
| **SyslogNet.Client** | Logging (UDP/TCP syslog) | Yes | **Med** | **Gap** | `provider:"syslog"` `operation:"send"` `methods:["Send"]` `receiverTypes:["SyslogNet.Client.SyslogSender","SyslogNet.Client.UdpSyslogSender","SyslogNet.Client.TcpSyslogSender"]`. Confirmed: `SyslogTcpSender.cs`, `SyslogLogger.cs` in NHS GP Connect. Network I/O. |
| **Serilog.Sinks.Graylog** | Logging (network sink) | Yes | **Low** | **Gap** | `provider:"graylog"` `operation:"send"` — Serilog sink, configured via `LoggerConfiguration`; actual UDP/TCP send is internal to the sink. Hard to detect at call sites (it's a Serilog config call). Low priority: configured not called. Rule: `methods:["Graylog"]` on `Serilog.LoggerSinkConfigurationExtensions` as a DI/config signal rather than effect. |
| **PgpCore** | Crypto/signing (with file I/O) | Yes | **Low** | **Gap** | `provider:"crypto_pgp"` `operation:"encrypt"` `methods:["EncryptAsync","SignAndEncryptAsync","DecryptAsync"]` `receiverTypes:["PgpCore.PGP"]`. Confirmed: `BupaFeed/FileOperations.cs`, `Enigma.cs`. PgpCore operates on streams/files — filesystem I/O. Low priority (client-data-transformation tools). |
| **Microsoft.ClearScript** | Script engine (JS eval) | Yes | **Low** | **Gap** | `provider:"script_eval"` `operation:"eval"` `methods:["Evaluate","Execute","ExecuteDocument"]` `receiverTypes:["Microsoft.ClearScript.V8.V8ScriptEngine"]`. Confirmed: `FormValidation.cs`, `Main_Medicare.cs`. Executes arbitrary JavaScript in-process — meaningful side-effect surface. Low priority (narrow use). |
| **LibGit2Sharp** | Source control (filesystem) | Yes | **Low** | **Gap** | `provider:"git"` `operation:"repository"` `methods:["Clone","Fetch","Commit","Push"]` `receiverTypes:["LibGit2Sharp.Repository"]`. Only confirmed use: `Tools.SqlRunner/Repository.cs` (a tool, not the main app). Very low value for main app analysis. |

---

## Already covered

Libraries that map directly to existing rig providers (builtin-rules.json or rig.rules.json):

| Library | rig provider | Coverage status |
|---|---|---|
| **MailKit** (SMTP) | `smtp` | Covered — SmtpClient.Connect/Send/Disconnect |
| **System.Net.Mail** | `smtp` | Covered — SmtpClient.Send/SendMailAsync |
| **Flurl.Http** | `http` | Covered — GeneratedExtensions GET/POST/send |
| **System.Net.Http.HttpClient** | `http` | Covered — GetAsync/PostAsync/SendAsync etc. |
| **System.Net.WebClient** | `http` | Covered — UploadString/DownloadString etc. |
| **System.Net.WebRequest / HttpWebRequest** | `http` | Covered — GetResponse/GetRequestStream |
| **System.Net.Sockets.Socket / TcpClient** | `socket` | Covered — Connect/Send/Receive |
| **Microsoft.EntityFrameworkCore** | `efcore` | Covered — ToListAsync/SaveChanges/FromSqlRaw/Migrate etc. |
| **SD.LLBLGen.Pro.ORMSupportClasses** | `llblgen` | Covered — Save/Delete/GetMulti/Commit etc. |
| **StackExchange.Redis** | `redis` | Covered — StringGet/Set/HashGet/KeyDelete |
| **MMS.Redis** (internal) | `queue` | Covered — Enqueue/PublishToChannel |
| **Azure.Storage.Blobs** | `azure_blob` | Covered — UploadAsync/DownloadAsync/DeleteAsync |
| **AWSSDK.S3** | `aws_s3` | Covered — PutObjectAsync/GetObjectAsync/DeleteObjectAsync |
| **Azure.Messaging.EventGrid** | `eventbus` | Partial (see Partial column above) |
| **MedDBase.DistributedFileService.DFS** | `object_store` | Covered — Save/Load/Delete |
| **Echo.Process** | `echo_publish` | Covered — tell/ask/tellChild |
| **Polly** | `resilience` | Covered — ResiliencePipeline.Execute/ExecuteAsync |
| **System.IO.File / Directory** | `io` | Covered — ReadAllText/WriteAllText/Create etc. |
| **System.IO.StreamReader/Writer** | `io` | Covered — Read/Write/Flush |
| **System.Xml.XmlDocument / XDocument** | `io` | Covered — Save |
| **System.Threading.Monitor/SemaphoreSlim** | `lock` | Covered — Enter/Exit/WaitAsync |
| **System.Diagnostics.Process** | `process` | Covered — Start |
| **System.Runtime.Caching.MemoryCache** | `inproc_cache` | Covered — Get/Set/Remove |
| **Microsoft.Extensions.Caching.Memory** | `inproc_cache` | Covered — GetOrCreate/Set etc. |
| **System.Data.Common.DbConnection** | `db_connection` | Covered — Open/OpenAsync |
| **System.Data.Common.DbDataReader** | `db_reader` | Covered — Read/ReadAsync |
| **RabbitMQ.Client** | `rabbitmq` | Covered (not used in MedDBase but rule exists) |
| **Google.Cloud.Storage.V1** | `object_store` (via DFS wrapper) | Covered via `DFS.Save/Load/Delete` app-level rule |
| **Hl7.Fhir.R4 / Rest** | `http` | Partial — FhirClient wraps HttpClient; http:GET/POST rule fires on the underlying HttpClient calls |
| **Microsoft.AspNet.SignalR** | `signalr` EP | Covered as entry-point (hub) |
| **MedDBase.Application.Workflows.HealthcodeWebServices** | `soap` | Covered — submitBill/requestRegistration etc. |

---

## Non-I/O (excluded)

These libraries are confirmed as pure-compute, DI, serialization, test infrastructure, build/analyzer, or UI rendering with no external-boundary crossing. Listed as brief rollups.

| Category | Count | Examples |
|---|---|---|
| **Serialization / data format** | ~20 | Newtonsoft.Json, System.Text.Json, Google.Protobuf, Apache.Arrow, NodaTime, System.Xml.*, SharpYaml, NJsonSchema |
| **DI / IoC** | ~8 | Ninject, Ninject.Extensions.Factory, Microsoft.Extensions.DependencyInjection, CommonServiceLocator |
| **Testing** | ~25 | xunit, NSubstitute, Bogus, Shouldly, Snapshooter, Allure.Xunit, JunitXml, CompareNETObjects, Microsoft.Playwright (test runner), DiffEngine, EmptyFiles |
| **Build / codegen / analyzer** | ~20 | Microsoft.CodeAnalysis, NSwag.MSBuild, CSharpier.Core, Microsoft.TypeScript.MSBuild, docfx.console, JetBrains.Annotations, Microsoft.Build.* |
| **OpenAPI / Swagger** | ~8 | Swashbuckle.AspNetCore, NSwag.Generation, NSwag.Annotations, Microsoft.OpenApi |
| **Logging infrastructure** | ~12 | Serilog (core), Serilog.Sinks.Console, Serilog.Sinks.File, Serilog.AspNetCore, prometheus-net (metrics — no I/O at call site level), Syslog.Framework.Logging (config-only), Microsoft.ApplicationInsights (not called in src/) |
| **Crypto / PKI (pure-compute)** | ~10 | BouncyCastle.Cryptography, System.Security.Cryptography.*, Pkcs11Interop, System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml, System.Formats.Asn1 |
| **UI / rendering (pure)** | ~15 | SkiaSharp, SixLabors.ImageSharp, SixLabors.Fonts, NetBarcode, QRCoder, MathNet.Numerics, LanguageExt.*, FSharp.Core, CommonMark.NET |
| **Document processing (pure transform)** | ~10 | DocumentFormat.OpenXml, PdfPig, OpenMcdf, RtfPipe, HtmlAgilityPack, MsgReader, IFilterTextReader |
| **Identity / auth (token handling, pure)** | ~15 | Microsoft.Identity.Client, IdentityModel, IdentityModel.OidcClient, Microsoft.IdentityModel.Tokens, System.IdentityModel.Tokens.Jwt, AspNetSaml, Microsoft.Owin.Security.* |
| **Utility / pure-compute** | ~30 | LanguageExt, NodaTime, Polly (wrapper, not I/O itself), libphonenumber-csharp, Pluralize.NET, Base62-Net, IPAddressRange, TimeZoneConverter, Humanizer.Core, LinqToExcel (pure Excel parse), DbfDataReader (pure file parse), Lucene.Net (in-process index — no confirmed call sites) |
| **Frontend / JS assets** | ~15 | angularjs, jQuery, bootstrap, Modernizr, popper.js, Twitter.Bootstrap, WebGrease |
| **Platform / BCL polyfills** | ~80 | System.Buffers, System.Memory, System.Runtime.*, System.Collections.*, System.Threading.*, NETStandard.Library, runtime.* native packages |

**Total non-I/O / excluded: ~268 packages** (out of 549 total).

---

## Top gaps

The following are the highest-value missing detectors, in priority order:

1. **Dapper** — `dapper:query/execute`  
   Used in `AuditsRepository` (insert audit events + query) and `ObservationRequestHelpers`. Raw SQL over `IDbConnection` is completely invisible to rig today; any code path that writes to SQL via Dapper shows zero DB effects. Highest value: 4 confirmed call sites across production service code.

2. **Google.Cloud.PubSub.V1** — `gcp_pubsub:publish`  
   The main `GcpPubSubAuditLog` and `MainAppAuditLog` publish audit events to GCP Pub/Sub. This is the live audit trail for the production system. Without this detector, every code path that triggers an audit appears to have no external publish side-effect.

3. **Twilio** — `twilio:sms`  
   Dedicated SMS service (`src/sms/`) with `TwilioDriver`, plus `TwilioAPIWrapper` in the main service tier. SMS sends are real external effects (charged, irreversible). 10 call-site files confirmed.

4. **SendGrid** — `sendgrid:send`  
   Transactional email via SendGrid HTTP API used in the mail-merge service. Currently the only email detector covers SMTP (MailKit); SendGrid is a separate HTTP-based path completely missed by the smtp detector.

5. **Xero.NetStandard.OAuth2** — `xero:request`  
   28 files using the Xero accounting API (invoice creation, payment reconciliation, OAuth token management). This is the accountancy integration and represents high-business-value I/O that is currently invisible to rig.

6. **OpenAI** — `openai:complete`  
   New AI feature (`OpenAi.cs`). OpenAI calls are expensive and rate-limited; knowing which entry points reach them is important for blast-radius analysis. Single file but growing feature area.

7. **GoogleApi (Maps/Places)** — `google_maps:query`  
   Address fuzzy lookup used during patient/appointment data entry. Confirmed active usage. Knowing when this external API is called matters for latency/failure blast-radius.

8. **Dapper (extension) + Azure.Messaging.ServiceBus** — partial  
   Azure Service Bus is in the dep tree but no call sites were found in `src/`. Worth adding the rule now so it fires when the integration is built (per the DaisyBill PRD direction). Low risk; add the rule speculatively.

9. **System.DirectoryServices.Protocols (LDAP)** — `ldap:search`  
   NHS GP Connect uses LDAP to query the Spine Directory Service (`SpineDirectoryService.cs`). LDAP calls are external network I/O. Currently zero coverage.

10. **IronPdf / CefSharp** — `ironpdf:render` / `browser_render:navigate`  
    PDF rendering via headless Chrome is cross-process I/O. The PDF service is a standalone node (`src/pdf2/`, `src/pdf/`) — knowing which endpoints trigger a PDF render is relevant for performance and resource analysis.

---

## Appendix: packages referenced but apparently unused in app code

Based on grep scans finding no call sites in `src/**/*.cs` (excluding test projects):

| Package | Notes |
|---|---|
| `Azure.Messaging.ServiceBus` | Dep present; zero app call sites found. May be a transitive dep or future placeholder. |
| `Microsoft.Azure.ServiceBus` | Legacy SDK; same — zero app call sites. |
| `Microsoft.Azure.Cosmos` | Zero app call sites for `CosmosClient`. |
| `Microsoft.Graph` | Zero `GraphServiceClient` call sites. Likely pulled in transitively. |
| `Lucene.Net` | Zero `IndexWriter`/`FSDirectory` call sites in `src/`. May be dead code or used only in tools. |
| `Mindbox.Data.Linq` | Only assembly-info stubs in `MMS.Data.Linq`; no active `DataContext` call sites found. |
| `TikaOnDotnet.TextExtractor` | No `TikaServerClient` call sites found. |
| `MartinCostello.SqlLocalDb` | No `ISqlLocalDbApi` call sites. Likely test infrastructure. |
| `Azure.Security.KeyVault.*` | No `SecretClient` call sites in app code. |
