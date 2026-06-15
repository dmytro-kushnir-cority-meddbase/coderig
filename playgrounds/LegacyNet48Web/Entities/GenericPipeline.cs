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
}
