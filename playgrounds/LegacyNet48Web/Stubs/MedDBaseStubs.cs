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
