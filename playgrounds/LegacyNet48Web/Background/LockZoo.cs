using System.Threading;
using LegacyNet48Web.External;

namespace LegacyNet48Web.Background
{
    // Concurrency fixture for the lock-effect lifting (TODO #7). The C# `lock (x) {}` statement is
    // DEFINED by the language spec to lower to `Monitor.Enter(x, ref f); try {…} finally { Monitor.Exit(x); }`,
    // but the keyword carries no invocation SYNTAX — so without the synthetic-ref lowering in the
    // extractor a `lock {}` body would carry NO lock effect, while the explicit `Monitor.Enter(x)`
    // call below already does. These methods are the ground truth: the synthetic acquire/release a
    // `lock {}` produces must be the SAME lock:acquire / lock:release effects the explicit call yields.
    public sealed class LockZoo
    {
        private readonly object _gate = new object();
        private readonly object _inner = new object();
        private int _counter;

        // (1) plain lock over an in-memory mutation — no inner effect, just the lock itself.
        public void IncrementUnderLock()
        {
            lock (_gate)
            {
                _counter++;
            }
        }

        // (2) lock HELD ACROSS a SOAP call — the canonical "lock held across IO" smell that the
        //     ordering work (#8) will prove. The acquire must straddle the SubmitBill effect.
        public void SubmitUnderLock()
        {
            lock (_gate)
            {
                new HealthcodeServiceProxy().SubmitBill("<bill/>");
            }
        }

        // (3) nested locks — two distinct LockStatementSyntax in one method; both must lift.
        public void NestedLocks()
        {
            lock (_gate)
            {
                lock (_inner)
                {
                    _counter++;
                }
            }
        }

        // (4) explicit Monitor.Enter/Exit — ALREADY lifted by the existing lock rule (invocation
        //     syntax). The ground-truth comparator for the synthetic refs above.
        public void ExplicitMonitor()
        {
            Monitor.Enter(_gate);
            try
            {
                _counter++;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }
}
