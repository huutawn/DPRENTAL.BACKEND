
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
            var row1 = await context.Database.SqlQueryRaw<LegacyAuditReasonRow>(
                """SELECT "Result", "Reason" FROM "AuditLogs" WHERE "Id" = {0}""", auditId).SingleAsync();
            Assert.Equal("Success", row1.Result);
            Assert.Equal(reason, row1.Reason);

            await migrator.MigrateAsync(previous);
            string restoredResult = await context.Database.SqlQueryRaw<string>(
                """SELECT "Result" AS "Value" FROM "AuditLogs" WHERE "Id" = {0}""", auditId).SingleAsync();
            Assert.Equal($"Success: {reason}", restoredResult);

            await migrator.MigrateAsync(migration);
            var row2 = await context.Database.SqlQueryRaw<LegacyAuditReasonRow>(
                """SELECT "Result", "Reason" FROM "AuditLogs" WHERE "Id" = {0}""", auditId).SingleAsync();
            Assert.Equal(reason, row2.Reason);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task OrganizationAuditStatusMigrationBackfillsDeterministicStatusesThroughUpgradeAndRollback()
    {
        NpgsqlConnectionStringBuilder connection = new("Host=localhost;Port=5435;Username=postgres;Password=postgres")
        {
            Database = $"authservice_status_migration_{Guid.NewGuid():N}",
            Pooling = false
        };
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connection.ConnectionString).Options;
        await using AuthDbContext context = new(options);
        IMigrator migrator = context.GetService<IMigrator>();
        const string migration = "20261005025406_AddOrganizationAuditStatus";
        string previous = context.Database.GetMigrations().TakeWhile(id => id != migration).Last();

        Guid orgId = Guid.NewGuid();
        Guid regId = Guid.NewGuid();
        Guid resubmitId = Guid.NewGuid();
        Guid reminderId = Guid.NewGuid();
        Guid approveId = Guid.NewGuid();
        Guid activateId = Guid.NewGuid();
        Guid rejectId = Guid.NewGuid();
        Guid suspendId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        Guid failedSuspendId = Guid.NewGuid();

        try
        {
            await migrator.MigrateAsync(previous);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditLogs" ("Id", "Target", "Action", "Result", "Timestamp") VALUES
                ({0}, {9}, 'Organization_Register', 'Success', NOW()),
                ({1}, {9}, 'Organization_Resubmit', 'Success', NOW()),
                ({2}, {9}, 'Organization_ReviewReminder', 'Success', NOW()),
                ({3}, {9}, 'Organization_Approve', 'Success', NOW()),
                ({4}, {9}, 'Organization_Activate', 'Success', NOW()),
                ({5}, {9}, 'Organization_Reject', 'Success', NOW()),
                ({6}, {9}, 'Organization_Suspend', 'Success', NOW()),
                ({7}, {9}, 'Organization_ProfileUpdate', 'Success', NOW()),
                ({8}, {9}, 'Organization_Suspend', 'Failed', NOW());
                """,
                regId, resubmitId, reminderId, approveId, activateId, rejectId, suspendId, profileId, failedSuspendId, orgId.ToString());

            await migrator.MigrateAsync(migration);

            List<AuditLog> logs = await context.AuditLogs.AsNoTracking().ToListAsync();
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Pending, logs.Single(l => l.Id == regId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Pending, logs.Single(l => l.Id == resubmitId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Pending, logs.Single(l => l.Id == reminderId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Active, logs.Single(l => l.Id == approveId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Active, logs.Single(l => l.Id == activateId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Rejected, logs.Single(l => l.Id == rejectId).Status);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Suspended, logs.Single(l => l.Id == suspendId).Status);
            Assert.Null(logs.Single(l => l.Id == profileId).Status);
            Assert.Null(logs.Single(l => l.Id == failedSuspendId).Status);

            // Rollback test: verify column is dropped
            await migrator.MigrateAsync(previous);
            int columnCount = await context.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::integer AS "Value" FROM information_schema.columns
                WHERE table_name = 'AuditLogs' AND column_name = 'Status'
                """).SingleAsync();
            Assert.Equal(0, columnCount);

            // Re-upgrade test
            await migrator.MigrateAsync(migration);
            AuditLog reloaded = await context.AuditLogs.AsNoTracking().SingleAsync(l => l.Id == suspendId);
            Assert.Equal(Dprental.AuthService.Common.Enums.OrganizationStatus.Suspended, reloaded.Status);
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

    private sealed record LegacyAuditReasonRow(string Result, string? Reason);
}
