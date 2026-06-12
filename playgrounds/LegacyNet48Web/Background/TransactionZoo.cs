using System;
using LegacyNet48Web.External;

namespace LegacyNet48Web.Background
{
    // Ordering/nesting fixture for the resource_span observation (TODO #8). Mirrors the canonical
    // MedDBase shape (Master_HealthcodeServiceImpl.SubmitToHealthcode): a repeatable-read transaction
    // opened with `using (var tx = Transaction.New())` is held OPEN across a synchronous SOAP call —
    // the transaction "spans a network call". This is a lexical-nesting property: the SOAP effect is
    // inside the transaction-using scope. Ground truth by construction.

    // Stand-in for the LLBLGen transaction handle (the name contains "Transaction" so the
    // resource_span rule's scopeTypePatterns matches it).
    public sealed class FakeTransaction : IDisposable
    {
        public static FakeTransaction New() => new FakeTransaction();

        public void Commit() { }

        public void Dispose() { }
    }

    public sealed class TransactionZoo
    {
        // Transaction HELD ACROSS a SOAP submit — the network call sits lexically inside the
        // using(transaction) scope, so the deriver must attach a transaction_spans_effect observation
        // to the soap effect.
        public void SubmitInsideTransaction()
        {
            using (var transaction = FakeTransaction.New())
            {
                new HealthcodeServiceProxy().SubmitBill("<bill/>");
                transaction.Commit();
            }
        }

        // A SOAP submit OUTSIDE any transaction — the negative case: no transaction_spans_effect.
        public void SubmitWithoutTransaction()
        {
            new HealthcodeServiceProxy().SubmitBill("<bill/>");
        }
    }
}
