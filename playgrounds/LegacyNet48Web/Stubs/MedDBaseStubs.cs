// Minimal stubs for MedDBase infrastructure types
namespace MMS.Web.UI
{
    public abstract class ClientPage
    {
        protected virtual void OnLoad() { }
    }

    // A non-ClientPage UI base. Components/widgets inherit this, NOT ClientPage.
    // Methods on these can still carry [ClientAction], but they must NOT be treated
    // as page action entry points (the real over-match: ~1083 such methods in MedDBase).
    public abstract class ClientControl
    {
        protected virtual void OnInit() { }
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
