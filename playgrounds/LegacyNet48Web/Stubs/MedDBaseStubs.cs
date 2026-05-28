// Minimal stubs for MedDBase infrastructure types
namespace MMS.Web.UI
{
    public abstract class ClientPage
    {
        protected virtual void OnLoad() { }
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
