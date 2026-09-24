using Dprental.AuthService.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class AuthMigrationConsistencyTests
{
    [Fact]
    public void CurrentModelMatchesLatestMigrationSnapshot()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=authservice;Username=authservice;Password=unused")
            .Options;

        using AuthDbContext context = new(options);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
