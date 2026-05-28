// Minimal stubs for LLBLGen Pro 5.9
namespace SD.LLBLGen.Pro.ORMSupportClasses
{
    public abstract class EntityBase2
    {
        public bool Save() => false;
        public bool Save(bool refetchAfterSave) => false;
        public System.Threading.Tasks.Task<bool> SaveAsync() => System.Threading.Tasks.Task.FromResult(false);
        public bool Delete() => false;
        public bool Fetch() => false;
    }

    public abstract class EntityCollectionBase2<TEntity> : IEntityCollection2
    {
        public bool GetMulti(IPredicate filter) => false;
        public System.Threading.Tasks.Task<bool> GetMultiAsync(IPredicate filter) => System.Threading.Tasks.Task.FromResult(false);
    }

    public abstract class DataAccessAdapterBase
    {
        public bool SaveEntity(EntityBase2 entity) => false;
        public System.Threading.Tasks.Task<bool> SaveEntityAsync(EntityBase2 entity) => System.Threading.Tasks.Task.FromResult(false);
        public bool DeleteEntity(EntityBase2 entity) => false;
        public bool FetchEntity(EntityBase2 entity) => false;
        public System.Threading.Tasks.Task<bool> FetchEntityAsync(EntityBase2 entity) => System.Threading.Tasks.Task.FromResult(false);
        public bool GetMulti(IEntityCollection2 collection, IPredicate? filter) => false;
        public System.Threading.Tasks.Task<bool> GetMultiAsync(IEntityCollection2 collection, IPredicate? filter) => System.Threading.Tasks.Task.FromResult(false);
        public void StartTransaction(System.Data.IsolationLevel level, string name) { }
        public void Commit() { }
        public void Rollback() { }
    }

    public interface IPredicate { }
    public interface IEntityCollection2 { }
}
