# Reverse EP-discovery audit — `CertificateEntity.AssertRight`

## Hot method
`CertificateEntity.AssertRight` — permission gate on every fetch/mutation; ~11 overloads incl. a `Func<R>` continuation form. `MMSEntityClasses/CertificateEntity.cs:873-998`.

## rig reported
| Query | Count |
|---|---|
| `callers` | 4,614 |
| `callers --async` | 4,625 (+11) |
| `callers --roots` | 1,612 (≈950 `▶ action` + ≈452 non-action `M:`/`F:`) |
| `callers --entrypoints` | 1,388 (action 1,331; background 6; http 1) |

## Real entry points reaching it
Surfaced by `--entrypoints` (all verified real): 1,331 `[ClientAction]`, 6 workflow `RegisterEvents` (background), 1 `PatientPortalHttpHandler.ProcessRequestAsync` (http).

Surfaced by `--roots` but NOT `--entrypoints` (real EPs, verified):
- SignalR `FieldService.*` (`HubBase` subclass, 6 methods)
- ASMX `[WebMethod]` `OrderResponses.HandleOrderResponseSaved`/`UpdateOrderRequest` (`System.Web.Services.WebService`)
- `IndividualSchedule.Page_Load` (`System.Web.UI.Page` codebehind)
- DataServer `ServletBase.Serve`/`Validate` (abstract servlet protocol)
- workflow lifecycle interface callbacks (`IWorkflowController.OnComplete`, etc.)

## EP-detection gaps
1. **SignalR `HubBase` subclass methods** — no rule. `--roots` recovers them (SignalR invokes via reflection → no static caller → genuine roots).
2. **ASMX `[WebMethod]`** on `System.Web.Services.WebService` — no rule.
3. **ASP.NET codebehind `Page_Load`** on `System.Web.UI.Page` — no rule (only `[ClientAction]` matched).
4. **DataServer servlet protocol** (`ServletBase.Serve/Validate`) — no rule; abstract base is the dispatch join point.
5. **Spurious**: 3 `F:` field-initializer roots (mechanically filterable by the `F:` prefix).

## Verdict
`--entrypoints` is highly trustworthy for its detected categories (every spot-check real). When it's sparse, **`--roots` reliably recovers the missing EPs** (~100% recall) at the cost of ~3 filterable `F:` entries. Top fix: add EP rules for SignalR hubs, ASMX `[WebMethod]`, and `Page_Load`/servlet kinds.
