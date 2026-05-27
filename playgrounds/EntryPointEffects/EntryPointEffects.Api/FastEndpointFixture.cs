using System.Threading;
using System.Threading.Tasks;

namespace FastEndpoints
{
    public abstract class Endpoint<TRequest, TResponse>
    {
        public virtual void Configure() { }

        public virtual Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

namespace EntryPointEffects.Api.FastEndpointsFixture
{
    public sealed record CreateTeamRequest
    {
        public const string Route = "/fastendpoints/teams";
    }

    public sealed record CreateTeamResponse;

    public sealed class CreateTeamEndpoint : FastEndpoints.Endpoint<CreateTeamRequest, CreateTeamResponse>
    {
        public override void Configure()
        {
            Post(CreateTeamRequest.Route);
        }

        public override Task<CreateTeamResponse> ExecuteAsync(CreateTeamRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CreateTeamResponse());
        }

        private static void Post(string route) { }
    }
}
