using EntryPointEffects.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MailKit.Net.Smtp
{
    public sealed class SmtpClient
    {
        public Task ConnectAsync(string host, int port, bool useSsl) => Task.CompletedTask;

        public Task SendAsync(object message) => Task.CompletedTask;

        public Task DisconnectAsync(bool quit) => Task.CompletedTask;
    }
}

namespace MediatR
{
    public interface ISender
    {
        Task Send(object request);
    }

    public interface IPublisher
    {
        Task Publish(object notification);
    }

    public interface IMediator : ISender, IPublisher;
}

namespace Ardalis.SharedKernel
{
    public interface IRepository<T>
    {
        Task FirstOrDefaultAsync(object specification);

        Task AddAsync(T item);
    }
}

namespace EntryPointEffects.Api.Services
{
    public sealed record FixtureCommand;

    public sealed record FixtureNotification;

    public sealed class ComposedEffectFixture(
        AppDbContext db,
        MailKit.Net.Smtp.SmtpClient smtpClient,
        MediatR.IMediator mediator,
        Ardalis.SharedKernel.IRepository<Team> repository
    )
    {
        public async Task ObserveAsync(Team team)
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Teams (Name) VALUES ({team.Name})"
            );
            await db.Teams.FromSqlRaw("SELECT Id, Name FROM Teams").ToListAsync();

            await smtpClient.ConnectAsync("smtp.test", 25, false);
            await smtpClient.SendAsync(new object());
            await smtpClient.DisconnectAsync(true);

            await mediator.Send(new FixtureCommand());
            await mediator.Publish(new FixtureNotification());

            await repository.FirstOrDefaultAsync(new object());
            await repository.AddAsync(team);
        }
    }
}
