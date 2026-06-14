# Reverse EP-discovery audit — `DFS.Load`

## Hot method
`M:MedDBase.DistributedFileService.DFS.Load(System.Guid)` — `dfs/src/DFS.cs:169`. Static facade over an injectable `loadFunc`; called by anything loading a stored document/PDF/image.

## rig reported
| Query | Count |
|---|---|
| `callers` | 1,141 methods |
| `callers --roots` | 250 (234 `M:` + 16 `P:`) |
| `callers --entrypoints` | 246 (action 223, echoactor 7, pagehandler 12, background 2, http 1, page 1) |
| `callers --async` | +18 (recovers `GeneratePreview.Initialise`, `Global.PostStartup` via `Router.fromConfig` handoff) |

## Real entry points reaching it
`--entrypoints` correct for the dominant kind (`[ClientAction]`, Echo actors via `Router.fromConfig`, DocumentShareProcess.Inbox). Missed by `--entrypoints`, recovered by `--roots`:
- Web API `DownloadsController.ImageThumbnail`/`MedicalRaw`, `FileController.Get` (`System.Web.Http.ApiController` + `[HttpGet]`/`[Route]`)
- `EmailEntityLinkService.Run`/`SendService.Run` (`MedDBase.RemoteClientServices.ServiceBase<T>.Run(T)` override, ThreadStart-driven)
- `PathwayInstance.InstanceInbox` (Echo `spawn<S,T>` — name ≠ `Inbox`)
- `AppStartupProcesses.Startup` (implements `IService` directly, not abstract `ServiceBase`)

## EP-detection gaps
1. **Web API `ApiController` + `[HttpGet/Post/...]`** — absent from rules (only `IHttpHandler`/`HttpTaskAsyncHandler` covered). High-traffic doc-serving EPs.
2. **`MedDBase.RemoteClientServices.ServiceBase<T>.Run(T)` override** — not modeled (MailSendReceive timer service); the `meddbase.servicebase.startup` rule targets a different `ServiceBase` FQN.
3. **Echo `InstanceInbox` name miss** — `meddbase.echo.inbox` rule matches the exact name `Inbox`; this stateful `spawn<S,T>` handler is `InstanceInbox`.
4. **`IService.Startup` interface vs abstract `ServiceBase`** — direct interface implementors fall through.
5. **Spurious**: 16 `P:` property-getter roots (property-get call-site modeling), deep utility/factory methods, interface-declaration stubs.

## Verdict
`--entrypoints` substantially trustworthy for the dominant kind; `--roots` recovers the ~4 missed real EP classes. Top fixes: ApiController + `[Http*]` detection; `ServiceBase<T>.Run` override rule; loosen Echo inbox name convention.
