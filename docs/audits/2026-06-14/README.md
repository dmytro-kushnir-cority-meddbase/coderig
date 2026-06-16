# rig recall / EP-detection audits — 2026-06-14

Nine Sonnet sub-agent audits of `rig` against the MedDBase store (`C:\Git\meddbase-analysis`,
`--rules rig.rules.json`), each ground-truthing rig's output against MedDBase source
(`C:\Git\meddbase-main-application`). Findings consolidated into `../../rig-review-issues.md`
(section "Findings from the 2026-06-14 entry-point audits").

## Forward recall audits (effects/dispatch rig misses from an entry point)
- [recall-submit-to-healthcode.md](recall-submit-to-healthcode.md) — Healthcode claim submission (SOAP/queue/dispatch). Mostly trustworthy; 1 cross-project stitch miss.
- [recall-documentshare-handleauthenticate.md](recall-documentshare-handleauthenticate.md) — DocumentShareService msg handler. Delegate-consumer body gap (3) + UpdateMulti rule gap.
- [recall-pacs-processhl7lines.md](recall-pacs-processhl7lines.md) — PACS HL7 ingest. `Try<T>` monad delegate-body gap (file IO) + silent external-assembly boundary.
- [recall-medicare-verification.md](recall-medicare-verification.md) — Medicare AU verification. Flurl HTTP rule gap (codebase-wide) + clientpage_proxy boundary.
- [recall-webhooks-publishevent.md](recall-webhooks-publishevent.md) — Webhook publish. Interface dispatch resolves; httpClient.PostAsync hidden behind a Func wrapper.

## Reverse EP-discovery audits (does `callers --roots/--entrypoints` find the entry points?)
- [reverse-ep-assertright.md](reverse-ep-assertright.md) — CertificateEntity.AssertRight (4614 callers). `--roots` recovers SignalR/ASMX/Page_Load/servlet EPs that `--entrypoints` misses.
- [reverse-ep-dfs-load.md](reverse-ep-dfs-load.md) — DFS.Load (1141 callers). ApiController + ServiceBase<T>.Run + InstanceInbox name-miss not detected.
- [reverse-ep-workflowcontrollerbase-save.md](reverse-ep-workflowcontrollerbase-save.md) — WorkflowControllerBase.Save (798 callers). Base-virtual reverse dispatch PRECISE; background delegate-target EPs untagged.

## Cross-service comms inventory
- [cross-service-comms-inventory.md](cross-service-comms-inventory.md) — full inventory of inter-process/external comms besides Echo (HTTP/SOAP/WCF-server/TCP/UDP/SMTP/POP3/EventGrid/cloud-blob/FHIR/webhooks/ObjectStore) + confirmed absences (gRPC/MSMQ/RabbitMQ/ServiceBus/Kafka/SignalR/named-pipes).

## Status of fixes (see ../../rig-review-issues.md)
- **DONE (rule additions, verified)**: Flurl + WebClient HTTP, `XmlDocument.Save` io:write (builtin-rules.json); LLBLGen `UpdateMulti` write (meddbase rig.rules.json).
- **Open**: delegate/lambda-body tracing through wrappers/monads; EP-detector rules (SignalR/ApiController/WebMethod/Page_Load/servlet/background-delegate-target); cross-project stitch; boundary tagging; cross-service-comm detection feature.
