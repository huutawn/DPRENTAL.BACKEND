using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class EfAuthSessionRepositoryCrudTests
{
    [Fact]
    public async Task CrudMethodsPersistAndRemoveSession()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfAuthSessionRepository repository = new(dbContext);
        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
        };

        Session created = await repository.CreateAsync(session);
        Session? found = await repository.GetByIdAsync(session.Id);
        IReadOnlyList<Session> sessions = await repository.GetAllAsync();

        Assert.Same(session, created);
        Assert.NotNull(found);
        Assert.Single(sessions);

        session.RevokedAtUtc = DateTime.UtcNow;
        Assert.True(await repository.UpdateAsync(session));
        Assert.Equal(session.RevokedAtUtc, (await repository.GetByIdAsync(session.Id))!.RevokedAtUtc);

        Assert.True(await repository.DeleteAsync(session.Id));
        Assert.Null(await repository.GetByIdAsync(session.Id));
        Assert.False(await repository.DeleteAsync(session.Id));
    }
}
