namespace LegacyNet48Web.Dispatch
{
    // Fixture cases for EXACT Roslyn-mined dispatch facts (docs/HANDOFF-exact-dispatch-facts.md).
    // Each shape pins a correspondence that name/arity CHA matching cannot resolve exactly:
    //
    //  1. SAME-ARITY OVERLOADS on one interface (the real-world IWorkflows.Register bug): a call to
    //     Register(int, ControllerTask) must dispatch ONLY to the matching impl — name+arity matching
    //     sees two same-name, same-arity candidates and cross-contaminates them.
    //  2. GENERIC interface impl: IRepo`1.Store(`0) -> IntRepo.Store(System.Int32). The DocID param
    //     strings differ (`0 vs System.Int32), so only a semantic (Roslyn) mapping pairs them.
    //  3. OVERRIDE CHAIN: AlertBase.Raise <- EmailAlert.Raise <- PagerAlert.Raise. Immediate
    //     base->override hops are mined; the query-time forward closure must reach BOTH from the base.

    // --- 1. Same-arity overload pair -------------------------------------------------------------

    public sealed class ControllerTask { }

    public sealed class MasterTask { }

    public interface IDispatchWorkflows
    {
        void Register(int priority, ControllerTask controller);

        void Register(string name, MasterTask master);
    }

    public class WorkflowRegistry : IDispatchWorkflows
    {
        public void Register(int priority, ControllerTask controller)
        {
            ControllerRegistered();
        }

        public void Register(string name, MasterTask master)
        {
            MasterRegistered();
        }

        private void ControllerRegistered() { }

        private void MasterRegistered() { }
    }

    public class WorkflowCaller
    {
        // Binds EXACTLY to IDispatchWorkflows.Register(System.Int32, ControllerTask). The mined impl
        // edge must take this to WorkflowRegistry's matching overload (-> ControllerRegistered) and
        // NEVER to the MasterTask overload (-> MasterRegistered).
        public void RegisterController(IDispatchWorkflows workflows, ControllerTask task)
        {
            workflows.Register(1, task);
        }
    }

    // --- 2. Generic interface impl ----------------------------------------------------------------

    public interface IRepo<T>
    {
        void Store(T item);
    }

    public sealed class IntRepo : IRepo<int>
    {
        public void Store(int item)
        {
            StoredInt();
        }

        private void StoredInt() { }
    }

    public class RepoCaller
    {
        public void Use(IRepo<int> repo)
        {
            repo.Store(42);
        }
    }

    // --- 3. Override chain -------------------------------------------------------------------------

    public abstract class AlertBase
    {
        public virtual void Raise() { }
    }

    public class EmailAlert : AlertBase
    {
        public override void Raise() { }
    }

    public sealed class PagerAlert : EmailAlert
    {
        public override void Raise() { }
    }

    public class AlertCaller
    {
        // Receiver is the declaring base itself -> no receiver narrowing; the mined override-chain
        // closure must fan AlertBase.Raise out to BOTH overrides.
        public void Fire(AlertBase alert)
        {
            alert.Raise();
        }
    }
}
