
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
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

    [Fact]
    public async Task MigrateOnNewDatabaseAppliesAll24MigrationsAndSubsequentMigrateIsIdempotent()
    {
        string connStr = "Host=localhost;Port=5435;Database=authservice_fresh_migrate;Username=postgres;Password=postgres;Include Error Detail=true";
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connStr)
            .Options;

        await using AuthDbContext ctx = new(options);
        await ctx.Database.EnsureDeletedAsync();

        IEnumerable<string> pendingBefore = await ctx.Database.GetPendingMigrationsAsync();
        Assert.Equal(24, pendingBefore.Count());

        await ctx.Database.MigrateAsync();

        IEnumerable<string> applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Equal(24, applied.Count());

        IEnumerable<string> pendingAfter = await ctx.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingAfter);

        // Chạy lại migrate trên DB đã migrate: không áp dụng trùng, không lỗi
        await ctx.Database.MigrateAsync();

        IEnumerable<string> appliedAfterReapply = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Equal(24, appliedAfterReapply.Count());

        await ctx.Database.EnsureDeletedAsync();
    }
}
