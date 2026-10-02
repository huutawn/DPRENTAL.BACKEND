
using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class AuthMigrationConsistencyTests
{
    [Fact]
    public async Task OrganizationAuditReasonMigrationPreservesLegacyReasonThroughUpgradeAndRollback()
    {
        NpgsqlConnectionStringBuilder connection = new("Host=localhost;Port=5435;Username=postgres;Password=postgres")
        {
            Database = $"authservice_reason_migration_{Guid.NewGuid():N}",
            Pooling = false
        };
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connection.ConnectionString).Options;
        await using AuthDbContext context = new(options);
        IMigrator migrator = context.GetService<IMigrator>();
        const string migration = "20261002035104_AddOrganizationSuspensionAndAuditReason";
        string previous = context.Database.GetMigrations().TakeWhile(id => id != migration).Last();
        Guid auditId = Guid.NewGuid();
        const string reason = "Giấy phép kinh doanh hết hạn";

        try
        {
            await migrator.MigrateAsync(previous);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditLogs" ("Id", "Target", "Action", "Result", "Timestamp")
                VALUES ({0}, {1}, 'Organization_Reject', {2}, NOW())
                """, auditId, Guid.NewGuid().ToString(), $"Success: {reason}");

            await migrator.MigrateAsync(migration);
            AuditLog log = await context.AuditLogs.AsNoTracking().SingleAsync(l => l.Id == auditId);
            Assert.Equal("Success", log.Result);
            Assert.Equal(reason, log.Reason);

            await migrator.MigrateAsync(previous);
            string restoredResult = await context.Database.SqlQueryRaw<string>(
                """SELECT "Result" AS "Value" FROM "AuditLogs" WHERE "Id" = {0}""", auditId).SingleAsync();
            Assert.Equal($"Success: {reason}", restoredResult);

            await migrator.MigrateAsync(migration);
            Assert.Equal(reason, (await context.AuditLogs.AsNoTracking().SingleAsync(l => l.Id == auditId)).Reason);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

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
    public async Task MigrateOnNewDatabaseAppliesAllMigrationsAndSubsequentMigrateIsIdempotent()
    {
        string connStr = "Host=localhost;Port=5435;Database=authservice_fresh_migrate;Username=postgres;Password=postgres;Include Error Detail=true";
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connStr)
            .Options;

        await using AuthDbContext ctx = new(options);
        await ctx.Database.EnsureDeletedAsync();

        int migrationCount = ctx.Database.GetMigrations().Count();
        IEnumerable<string> pendingBefore = await ctx.Database.GetPendingMigrationsAsync();
        Assert.Equal(migrationCount, pendingBefore.Count());

        await ctx.Database.MigrateAsync();

        // Expand migration keeps URL data available for the offline import and reconciliation.
        int legacyColumns = await ctx.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*)::integer AS "Value" FROM information_schema.columns
            WHERE table_schema = 'auth' AND
              ((table_name = 'Users' AND column_name = 'AvatarUrl') OR
               (table_name = 'Organizations' AND column_name IN ('LogoUrl', 'BusinessLicenseDocumentUrl')))
            """).SingleAsync();
        Assert.Equal(3, legacyColumns);

        IEnumerable<string> applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Equal(migrationCount, applied.Count());

        IEnumerable<string> pendingAfter = await ctx.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingAfter);

        // Chạy lại migrate trên DB đã migrate: không áp dụng trùng, không lỗi
        await ctx.Database.MigrateAsync();

        IEnumerable<string> appliedAfterReapply = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Equal(migrationCount, appliedAfterReapply.Count());

        await ctx.Database.EnsureDeletedAsync();
    }
}
