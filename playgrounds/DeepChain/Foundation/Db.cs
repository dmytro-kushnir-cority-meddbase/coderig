namespace Foundation;

// Bottom of the stack: the DB effect sink. Reachability from the top entry point (Web.HomePage.Show)
// must reach Db.Query five project hops away, through an interface dispatch — mimics MedDBase's
// data-tier primitive sitting at the base of a deep reference closure.
public static class Db
{
    public static string Query(string sql) => $"rows for: {sql}";
}

public readonly struct Result<T>
{
    public Result(T value) => Value = value;

    public T Value { get; }
}
