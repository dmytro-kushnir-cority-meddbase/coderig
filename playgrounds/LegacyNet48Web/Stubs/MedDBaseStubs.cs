// Minimal stubs for MedDBase infrastructure types
namespace MMS.Web.UI
{
    public abstract class ClientPage
    {
        protected virtual void OnLoad() { }
    }

    // Base of every SOURCE-GENERATED navigation proxy (RequestResponseProxyGenerator emits
    // `<Page>Proxy : ProxyBase`). The generated nav methods (Show/ShowDialog/Redirect) call protected
    // helpers declared here; their bodies don't need to fully bind for rig to index the proxy TYPE +
    // base edge + public method declarations. The clientpage_proxy rule gates on this base type.
    public abstract class ProxyBase
    {
        protected ProxyBase(System.Type pageType) { }
        protected void AddWorkflow(object url) { }
        protected void LoadChildDialog(string className, string containerId, string url, string title) { }
        protected void ProcessEventDelegate(string id, System.Delegate handler, string name) { }
        protected static string ToStringForLink(object value) => value?.ToString();
    }

    // A non-ClientPage UI base. Components/widgets inherit this, NOT ClientPage.
    // Methods on these can still carry [ClientAction], but they must NOT be treated
    // as page action entry points (the real over-match: ~1083 such methods in MedDBase).
    public abstract class ClientControl
    {
        protected virtual void OnInit() { }
    }

    // The OTHER page family: PageLoad.Create() reflects + instantiates PageBase subclasses and
    // invokes their Initialise/OnAction lifecycle hooks (the legacy + login path). Not a ClientPage,
    // so the ClientPage rules never see it (gap G2). PageBase subclasses are page entry points, and
    // their reflection-invoked Initialise/OnAction are handler entry points.
    public abstract class PageBase
    {
        public virtual void Initialise() { }
        public virtual void OnAction() { }
    }
}

namespace MMS.Web.UI.Attributes
{
    // Marks a ClientPage method as a client-callable action endpoint.
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class ClientActionAttribute : System.Attribute
    {
    }
}

namespace MedDBase.Nucleus.Interfaces.Services
{
    public abstract class ServiceBase
    {
        public abstract void Startup();
        protected virtual void Shutdown() { }
    }
}

namespace MedDBase.Application.Core.Background
{
    public interface IBackgroundProcess
    {
        void Process();
    }
}

namespace MedDBase.Application.Workflows
{
    // A framework base whose virtual lifecycle hooks are the real entry points: a subclass becomes
    // reachable only through the methods it OVERRIDES (the requireOverride classInheritance shape).
    public abstract class WorkflowControllerBase
    {
        public virtual void OnSave() { }
        public virtual void OnCancel() { }
    }
}

namespace MedDBase.Wcf
{
    // A FIRST-PARTY OperationContract stand-in. The real System.ServiceModel.OperationContract is
    // third-party and dropped by the runtime-assembly filter, so a first-party attribute is what
    // lets the fixture exercise the attribute-gated (baseTypes:["*"]) WCF classInheritance rule.
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class OperationContractAttribute : System.Attribute
    {
    }
}

namespace MedDBase.Messages
{
    public interface IChamberMsg
    {
        System.Guid ChamberGuid { get; }
    }

    public static class ChamberMsgExtensions
    {
        public static void tell<T>(this T msg) where T : IChamberMsg { }
        public static System.Threading.Tasks.Task<TReply> ask<T, TReply>(this T msg) where T : IChamberMsg
            => System.Threading.Tasks.Task.FromResult(default(TReply)!);
    }
}
