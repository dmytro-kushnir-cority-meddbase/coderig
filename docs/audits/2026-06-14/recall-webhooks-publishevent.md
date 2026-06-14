# Recall audit — `IWebhooks.PublishEvent`

## Entry point
`M:MedDBase.Application.Core.Services.IWebhooks.PublishEvent(...)` — `MedDBase.Application.Core.Interfaces/Services/IWebhooks.cs:8`. Concrete impl `MedDBase.ServiceLayer.Webhooks.Webhooks`; delivery in separate IIS service `MedDBase.Webhooks`.

## rig reported
From `IWebhooks.PublishEvent`: 348 reachable, 3 direct effects (2 queue publish Redis, 1 queue write Redis) + 63 dispatch fan-out. **Interface dispatch resolves** (`IWebhooks` → concrete `Webhooks` ✓). From `Publisher.Publish`: 2 effects (1 Polly resilience execute, 1 throw), **0 HTTP**. From `MonitorQueueBackgroundService.ExecuteAsync`: reaches `Publisher.Publish`/`WebhookHttpClient.Send`.

## Confirmed misses
**1. `httpClient.PostAsync` (outbound POST) completely invisible — delegate-body gap through a wrapper.**
- `WebhookHttpClient.cs:46`: `Histogram.Log1Async(uriStr, () => httpClient.PostAsync(uri, jsonContent, ct))`. `rig reaches "WebhookHttpClient.Send"` → 0 effects; tree terminates at `Metrics.Log1Async`/`Metrics.Go`.
- Why: the HTTP call lives inside a `Func<...>` lambda passed to `Histogram.Log1Async`; rig doesn't trace through the `Func<>` arg into the lambda body (same class as `project_coderig_delegate_consumer`). The single most important effect in the webhook service.

**3. LanguageExt `memo()` Func body not traced — `SubscriberManager.GetEndpoint`.** `cache = memo<...>((args) => …)` (`SubscriberManager.cs:18`); the lambda body is opaque. No effect missed today (config-only LINQ), but latent.

**4. Polly `ResiliencePipeline.ExecuteAsync(async t => await SendToEndpoint(...))`** — tagged `resilience:execute`; `SendToEndpoint` is only reached via a synthetic edge. The ultimate hole is miss #1 (`PostAsync`).

## Boundaries (expected)
Cross-deployment Redis handoff (`PublishEvent` → Redis queue → `MonitorQueueBackgroundService` poll → `Publisher.Publish`, separate IIS); `HttpClient`/`SocketsHttpHandler`/TLS SDK internals; Polly internals; LanguageExt `MatchAsync` lambdas.

## Verdict
Interface dispatch + Redis queue effects correct; cross-deployment Redis handoff is an expected boundary. Top gap: **`httpClient.PostAsync` invisible** because a metrics wrapper accepts it as a `Func<>` lambda — a rule won't help; needs delegate-body tracing through wrappers.
