using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class EfUserRepositoryTests
{
    [Fact]
    public async Task TryCreateAsyncPersistsUserAndEmailExistsFindsIt()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfUserRepository repository = new(dbContext);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "tan.len@example.com",
            PasswordHash = "hash",
            FullName = "Huynh Tan Len",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        Assert.False(await repository.EmailExistsAsync(user.Email));
        Assert.True(await repository.TryCreateAsync(user));

        Assert.True(await repository.EmailExistsAsync(user.Email));
        Assert.False(await repository.EmailExistsAsync("other@example.com"));
        Assert.NotNull(await repository.GetByIdAsync(user.Id));
    }
}
