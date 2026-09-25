
using Dprental.AuthService.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class EfExternalLoginRepositoryTests
{
    [Fact]
    public async Task FindAsyncReturnsLinkWithUserForMatchingProviderAndSubjectOnly()
    {
        DbContextOptions<AuthDbContext> options = CreateOptions();
        await using AuthDbContext dbContext = new(options);
        User user = CreateUser("len@example.com");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        EfExternalLoginRepository repository = new(dbContext);
        await repository.AddAsync(CreateLink(user.Id, "Google", "sub-1"));

        ExternalLogin? found = await repository.FindAsync("Google", "sub-1");

        Assert.NotNull(found);
        Assert.Equal(user.Id, found.UserId);
        Assert.Equal("len@example.com", found.User.Email);
        Assert.Null(await repository.FindAsync("Google", "sub-2"));
        Assert.Null(await repository.FindAsync("GitHub", "sub-1"));
    }

    [Fact]
    public async Task CreatingUserWithExternalLoginPersistsBothInOneSave()
    {
        DbContextOptions<AuthDbContext> options = CreateOptions();
        await using AuthDbContext dbContext = new(options);
        User user = CreateUser("an@example.com");
        user.ExternalLogins.Add(CreateLink(user.Id, "Google", "sub-9"));

        await new EfUserRepository(dbContext).CreateAsync(user);

        ExternalLogin? found = await new EfExternalLoginRepository(dbContext).FindAsync("Google", "sub-9");
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.UserId);
        Assert.Equal(1, await dbContext.ExternalLogins.CountAsync());
    }

    private static DbContextOptions<AuthDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static User CreateUser(string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "hash",
            FullName = "Test User",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

    private static ExternalLogin CreateLink(Guid userId, string provider, string subject) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = provider,
            ProviderSubject = subject,
            CreatedAtUtc = DateTime.UtcNow
        };
}
