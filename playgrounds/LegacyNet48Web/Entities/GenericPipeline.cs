namespace LegacyNet48Web.Entities
{
    // Path-contextual generic substitution fixture (mirrors MetafieldDefinitionCacheService's
    // QueryResult / QueryPipeline shape). An OPEN forwarding receiver — `QueryPipeline<T, U>` inside
    // QueryResult<T, U>'s body — carries no concrete type of its own, but the tree should render it
    // with the CONCRETE args the entry pinned, resolved via the mined receiver-arg ordinals. The
    // binding must also propagate down a chain (QueryPipeline -> OrderedPipeline).
    public sealed class OrderedPipeline<T, U>
    {
        // A throw is a built-in effect, so the default `tree` (which prunes paths that reach no effect)
        // keeps the whole forwarding chain rooted at the concrete entry.
        public void Sort() => throw new System.InvalidOperationException();
    }

    public sealed class QueryPipeline<T, U>
    {
        public void Run()
        {
            // Deeper open-forwarding hop: OrderedPipeline<T, U> forwards QueryPipeline's own params.
            new OrderedPipeline<T, U>().Sort();
        }
    }

    public sealed class QueryResult<T, U>
    {
        private readonly QueryPipeline<T, U> _pipeline = new QueryPipeline<T, U>();

        public void Enumerate()
        {
            _pipeline.Run(); // open forwarding receiver QueryPipeline<T, U> (ordinals "0,1")
        }
    }

    // Concrete entry: pins T = PatientEntity, U = InvoiceEntity. The tree rooted here should read
    //   QueryResult<PatientEntity, InvoiceEntity>.Enumerate
    //     -> QueryPipeline<PatientEntity, InvoiceEntity>.Run
    //       -> OrderedPipeline<PatientEntity, InvoiceEntity>.Sort
    // rather than the placeholder <T, U> at every open-forwarding hop.
    public sealed class GenericPipelineDemo
    {
        public void RunConcretePipeline()
        {
            var result = new QueryResult<PatientEntity, InvoiceEntity>();
            result.Enumerate();
        }
    }

    // STATIC-FACTORY chain mirroring MedDBase's QueryResult/QueryPipeline.Create<TEntity, RRecord>: no value
    // receiver; the concrete types flow through METHOD type-argument inference. The body forwards a mix of
    // the enclosing TYPE param (TColumn) and the enclosing METHOD param (RRecord). The tree rooted at the
    // concrete entry should monomorphize the whole chain.
    public sealed class StaticOrderedPipeline<TRecord, TColumn>
    {
        public static StaticOrderedPipeline<TRecord, TColumn> Sort<TEntity>() => throw new System.InvalidOperationException();
    }

    public sealed class StaticPipeline<TRecord, TColumn>
    {
        public static StaticPipeline<RRecord, TColumn> Build<TEntity, RRecord>() =>
            StaticOrderedPipeline<RRecord, TColumn>.Sort<TEntity>() == null ? null : null;
    }

    public sealed class StaticResult<TRecord, TColumn>
    {
        public static StaticResult<RRecord, TColumn> Build<TEntity, RRecord>() =>
            StaticPipeline<RRecord, TColumn>.Build<TEntity, RRecord>() == null ? null : null;
    }

    // Concrete static-factory entry. The tree should read:
    //   StaticResult<X, PatientEntity>.Build<DataAdapter, X>   (X = the inferred RRecord)
    //     -> StaticPipeline<X, PatientEntity>.Build<DataAdapter, X>
    //       -> StaticOrderedPipeline<X, PatientEntity>.Sort<DataAdapter>
    // i.e. PatientEntity (TColumn, declaring) and DataAdapter (TEntity, method) propagate down static calls.
    public sealed class StaticPipelineDemo
    {
        public void RunStaticPipeline() => StaticResult<InvoiceEntity, PatientEntity>.Build<DataAdapter, InvoiceEntity>();
    }
}
