// Minimal stubs so Roslyn can resolve MVC types without a NuGet reference
namespace System.Web.Http
{
    public abstract class ApiController { }

    public sealed class HttpGetAttribute : Attribute { }

    public sealed class HttpPostAttribute : Attribute { }

    public sealed class HttpPutAttribute : Attribute { }

    public sealed class HttpDeleteAttribute : Attribute { }

    public sealed class RouteAttribute : Attribute
    {
        public RouteAttribute(string template) { }
    }

    public sealed class RoutePrefixAttribute : Attribute
    {
        public RoutePrefixAttribute(string prefix) { }
    }

    public sealed class FromBodyAttribute : Attribute { }

    public class IHttpActionResult { }

    public class HttpResponseMessage { }
}
