
using Dprental.AuthService.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class EfUserRepositoryTests
{
    [Fact]
    public async Task CreateAsyncPersistsUserAndEmailExistsFindsIt()
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
        Assert.Same(user, await repository.CreateAsync(user));

        Assert.True(await repository.EmailExistsAsync(user.Email));
        Assert.False(await repository.EmailExistsAsync("other@example.com"));
        Assert.NotNull(await repository.GetByIdAsync(user.Id));
    }

    [Fact]
    public async Task GetUsersPagedAsync_RoleFiltering_InMemory_FiltersCorrectly()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfUserRepository repository = new(dbContext);

        Role adminRole = new() { Id = Guid.NewGuid(), Name = "Admin" };
        Role customerRole = new() { Id = Guid.NewGuid(), Name = "Customer" };
        dbContext.Roles.AddRange(adminRole, customerRole);

        User user1 = new()
        {
            Id = Guid.NewGuid(),
            Email = "u1@example.com",
            PasswordHash = "hash",
            FullName = "User One",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true
        };
        user1.UserRoles.Add(new UserRole { UserId = user1.Id, RoleId = adminRole.Id });

        User user2 = new()
        {
            Id = Guid.NewGuid(),
            Email = "u2@example.com",
            PasswordHash = "hash",
            FullName = "User Two",
            CreatedAt = DateTime.UtcNow,
            IsActive = false
        };
        user2.UserRoles.Add(new UserRole { UserId = user2.Id, RoleId = customerRole.Id });

        dbContext.Users.AddRange(user1, user2);
        await dbContext.SaveChangesAsync();

        // Filter by Admin role
        var (adminItems, adminCount) = await repository.GetUsersPagedAsync(0, 10, null, "Admin");
        Assert.Equal(1, adminCount);
        Assert.Equal(user1.Id, Assert.Single(adminItems).Id);

        // Filter by Customer role (case-insensitive "customer")
        var (customerItems, customerCount) = await repository.GetUsersPagedAsync(0, 10, null, "customer");
        Assert.Equal(1, customerCount);
        Assert.Equal(user2.Id, Assert.Single(customerItems).Id);
    }

    [Fact]
    public async Task GetByIdWithAccessAsync_LockedUser_ReturnsUser()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfUserRepository repository = new(dbContext);

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "locked@example.com",
            PasswordHash = "hash",
            FullName = "Locked User",
            CreatedAt = DateTime.UtcNow,
            IsActive = false // Locked!
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        User? reloaded = await repository.GetByIdWithAccessAsync(user.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsActive);
    }
}
