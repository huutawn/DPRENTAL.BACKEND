
using AuthService.Domain.Migrations;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class AuthMigrationConsistencyTests
{
    private const string OwnerMigration = "20261008070133_RepairOwnerRoleSeed";
    private const string OpsMigration = "20261008070247_RemoveOpsRole";
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid OpsRoleId = new("d4040000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("intact")]
    [InlineData("role")]
    [InlineData("permission")]
    [InlineData("grant")]
    public async Task OwnerSeedRepairRestoresMissingDataAndPreservesAccounts(string missing)
    {
        await WithMigrationDatabaseAsync(OwnerMigration, async (context, migrator) =>
        {
            User user = new() { Id = Guid.NewGuid(), Email = "owner-seed@example.test", FullName = "Seed test" };
            Role customer = await context.Roles.SingleAsync(r => r.Name == SystemRoleCode.Customer);
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customer.Id });
            context.Users.Add(user);
            if (missing != "role")
            {
                Organization org = new() { Id = Guid.NewGuid(), Name = "Seed test", Slug = "seed-test", Status = OrganizationStatus.Active };
                context.Organizations.Add(org);
                context.OrganizationMemberships.Add(new OrganizationMembership
                {
                    Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = org.Id,
                    RoleId = OwnerRoleId, Status = MembershipStatus.Active
                });
                Role owner = await context.Roles.SingleAsync(r => r.Id == OwnerRoleId);
                owner.Description = "Preserve existing description";
                Permission extra = new() { Id = Guid.NewGuid(), Code = "test.owner.extra", Description = "Preserve existing grant" };
                context.Permissions.Add(extra);
                context.RolePermissions.Add(new RolePermission { RoleId = OwnerRoleId, PermissionId = extra.Id });
            }
            await context.SaveChangesAsync();

            if (missing == "role")
                await context.Database.ExecuteSqlRawAsync("DELETE FROM auth.\"Roles\" WHERE \"Id\" = {0}", OwnerRoleId);
            if (missing == "permission")
                await context.Database.ExecuteSqlRawAsync("DELETE FROM auth.\"Permissions\" WHERE \"Code\" = 'inventory.asset.manage'");
            if (missing == "grant")
                await context.Database.ExecuteSqlRawAsync("DELETE FROM auth.\"RolePermissions\" WHERE \"RoleId\" = {0}", OwnerRoleId);

            string accountsBefore = await AccountSnapshotAsync(context);
            await migrator.MigrateAsync(OwnerMigration);
            Role repaired = await context.Roles.AsNoTracking().SingleAsync(r => r.Id == OwnerRoleId);
            Assert.Equal(SystemRoleCode.Owner, repaired.Name);
            Assert.Equal(RoleScope.Organization, repaired.Scope);
            if (missing != "role") Assert.Equal("Preserve existing description", repaired.Description);
            string[] required =
            [
                PermissionCode.IdentityOrganizationsRead, PermissionCode.IdentityOrganizationsUpdate,
                PermissionCode.IdentityMembersRead, PermissionCode.IdentityMembersManage,
                PermissionCode.IdentityRolesRead, PermissionCode.IdentityRolesAssignOrganization,
                PermissionCode.IdentityRolesRevokeOrganization, PermissionCode.InventoryAssetManage,
                PermissionCode.InventoryCalendarRead
            ];
            string[] actual = await context.RolePermissions.Where(rp => rp.RoleId == OwnerRoleId)
                .Select(rp => rp.Permission.Code).ToArrayAsync();
            Assert.Empty(required.Except(actual, StringComparer.Ordinal));
            if (missing is "intact" or "permission") Assert.Contains("test.owner.extra", actual);
            Assert.DoesNotContain(PermissionCode.IdentityRolesAssignGlobal, actual);
            Assert.DoesNotContain(PermissionCode.InventoryAllocationWrite, actual);
            Assert.False(await context.Roles.AnyAsync(r => r.Name == "own"));
            Assert.Equal(accountsBefore, await AccountSnapshotAsync(context));

            string seedAfter = await SeedSnapshotAsync(context);
            // Replay the migration body, not merely EF's already-applied history check.
            await context.Database.ExecuteSqlRawAsync(new RepairOwnerRoleSeed().UpOperations.OfType<SqlOperation>().Single().Sql);
            Assert.Equal(seedAfter, await SeedSnapshotAsync(context));
            await migrator.MigrateAsync(OwnerMigration);
            Assert.Equal(accountsBefore, await AccountSnapshotAsync(context));
        });
    }

    [Theory]
    [InlineData("name")]
    [InlineData("scope")]
    [InlineData("id")]
    [InlineData("permission")]
    public async Task OwnerSeedConflictRollsBackAllChanges(string conflict)
    {
        await WithMigrationDatabaseAsync(OwnerMigration, async (context, migrator) =>
        {
            switch (conflict)
            {
                case "name":
                    await context.Database.ExecuteSqlRawAsync("UPDATE auth.\"Roles\" SET \"Name\" = 'conflicting' WHERE \"Id\" = {0}", OwnerRoleId);
                    break;
                case "scope":
                    await context.Database.ExecuteSqlRawAsync("UPDATE auth.\"Roles\" SET \"Scope\" = 0 WHERE \"Id\" = {0}", OwnerRoleId);
                    break;
                case "id":
                    await context.Database.ExecuteSqlRawAsync("DELETE FROM auth.\"Roles\" WHERE \"Id\" = {0}", OwnerRoleId);
                    context.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "Owner", Scope = RoleScope.Organization });
                    await context.SaveChangesAsync();
                    break;
                case "permission":
                    await context.Database.ExecuteSqlRawAsync("DELETE FROM auth.\"Roles\" WHERE \"Id\" = {0}", OwnerRoleId);
                    await context.Database.ExecuteSqlRawAsync("UPDATE auth.\"Permissions\" SET \"Code\" = 'conflicting.permission' WHERE \"Code\" = 'inventory.calendar.read'");
                    break;
            }
            string before = await SeedSnapshotAsync(context);
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(OwnerMigration));
            Assert.Contains("seed conflict", error.MessageText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await SeedSnapshotAsync(context));
            Assert.DoesNotContain(OwnerMigration, await context.Database.GetAppliedMigrationsAsync());
        });
    }

    [Fact]
    public async Task RemovingOpsPreservesUsersOtherRolesAndRevokesOnlyAffectedSessions()
    {
        await WithMigrationDatabaseAsync(OpsMigration, async (context, migrator) =>
        {
            Role customer = await context.Roles.SingleAsync(r => r.Name == SystemRoleCode.Customer);
            User affected = new() { Id = Guid.NewGuid(), Email = "ops@example.test", FullName = "Ops user" };
            User other = new() { Id = Guid.NewGuid(), Email = "other@example.test", FullName = "Other user" };
            affected.UserRoles.Add(new UserRole { UserId = affected.Id, RoleId = OpsRoleId });
            affected.UserRoles.Add(new UserRole { UserId = affected.Id, RoleId = customer.Id });
            other.UserRoles.Add(new UserRole { UserId = other.Id, RoleId = customer.Id });
            context.Users.AddRange(affected, other);
            DateTime alreadyRevoked = DateTime.UtcNow.AddDays(-1);
            Session active = new() { Id = Guid.NewGuid(), UserId = affected.Id, ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
            Session revoked = new() { Id = Guid.NewGuid(), UserId = affected.Id, RevokedAtUtc = alreadyRevoked };
            Session unaffected = new() { Id = Guid.NewGuid(), UserId = other.Id, ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
            context.Sessions.AddRange(active, revoked, unaffected);
            await context.SaveChangesAsync();
            DateTime persistedRevocation = (await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == revoked.Id)).RevokedAtUtc!.Value;
            int permissionCount = await context.Permissions.CountAsync();
            int otherGrantCount = await context.RolePermissions.CountAsync(rp => rp.RoleId != OpsRoleId);

            await migrator.MigrateAsync(OpsMigration);
            Assert.False(await context.Roles.AnyAsync(r => r.Name == "ops" || r.Id == OpsRoleId));
            Assert.False(await context.UserRoles.AnyAsync(ur => ur.RoleId == OpsRoleId));
            Assert.False(await context.RolePermissions.AnyAsync(rp => rp.RoleId == OpsRoleId));
            Assert.Equal(2, await context.Users.CountAsync());
            Assert.Equal(2, await context.UserRoles.CountAsync(ur => ur.RoleId == customer.Id));
            Assert.Equal(permissionCount, await context.Permissions.CountAsync());
            Assert.Equal(otherGrantCount, await context.RolePermissions.CountAsync());
            Assert.NotNull((await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == active.Id)).RevokedAtUtc);
            Assert.Equal(persistedRevocation, (await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == revoked.Id)).RevokedAtUtc);
            Assert.Null((await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == unaffected.Id)).RevokedAtUtc);
            string after = await SeedSnapshotAsync(context);
            await context.Database.ExecuteSqlRawAsync(new RemoveOpsRole().UpOperations.OfType<SqlOperation>().Single().Sql);
            Assert.Equal(after, await SeedSnapshotAsync(context));
            await migrator.MigrateAsync(OpsMigration);
            Assert.Equal(after, await SeedSnapshotAsync(context));
        });
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("membership")]
    public async Task OpsRemovalRejectsUnexpectedReferencesWithoutDeletingData(string conflict)
    {
        await WithMigrationDatabaseAsync(OpsMigration, async (context, migrator) =>
        {
            if (conflict == "scope")
                await context.Database.ExecuteSqlRawAsync("UPDATE auth.\"Roles\" SET \"Scope\" = 1 WHERE \"Id\" = {0}", OpsRoleId);
            else
            {
                User user = new() { Id = Guid.NewGuid(), Email = "ops-member@example.test", FullName = "Unexpected member" };
                Organization org = new() { Id = Guid.NewGuid(), Name = "Unexpected", Slug = "unexpected" };
                context.Users.Add(user);
                context.Organizations.Add(org);
                context.OrganizationMemberships.Add(new OrganizationMembership
                {
                    Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = org.Id,
                    RoleId = OpsRoleId, Status = MembershipStatus.Active
                });
                await context.SaveChangesAsync();
            }
            string before = await SeedSnapshotAsync(context);
            await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(OpsMigration));
            Assert.Equal(before, await SeedSnapshotAsync(context));
            Assert.DoesNotContain(OpsMigration, await context.Database.GetAppliedMigrationsAsync());
        });
    }

    [Theory]
    [InlineData(OwnerMigration)]
    [InlineData(OpsMigration)]
    public async Task RoleDataMigrationsRejectUnsafeDowngrade(string migration)
    {
        await WithMigrationDatabaseAsync(migration, async (context, migrator) =>
        {
            string previous = context.Database.GetMigrations().TakeWhile(id => id != migration).Last();
            await migrator.MigrateAsync(migration);
            string before = await SeedSnapshotAsync(context);
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(before, await SeedSnapshotAsync(context));
            Assert.Contains(migration, await context.Database.GetAppliedMigrationsAsync());
        });
    }

    private static async Task WithMigrationDatabaseAsync(string migration, Func<AuthDbContext, IMigrator, Task> test)
    {
        NpgsqlConnectionStringBuilder connection = new("Host=localhost;Port=5435;Username=postgres;Password=postgres")
        {
            Database = $"authservice_role_seed_{Guid.NewGuid():N}", Pooling = false
        };
        await using AuthDbContext context = new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connection.ConnectionString).Options);
        IMigrator migrator = context.GetService<IMigrator>();
        try
        {
            string previous = context.Database.GetMigrations().TakeWhile(id => id != migration).Last();
            await migrator.MigrateAsync(previous);
            await test(context, migrator);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static Task<string> AccountSnapshotAsync(AuthDbContext context) => context.Database.SqlQueryRaw<string>(
        """
        SELECT jsonb_build_object(
            'users', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."Users" t),
            'organizations', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."Organizations" t),
            'userRoles', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "UserId", "RoleId") FROM auth."UserRoles" t),
            'memberships', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."OrganizationMemberships" t),
            'sessions', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."Sessions" t)
        )::text AS "Value"
        """).SingleAsync();

    private static async Task<string> SeedSnapshotAsync(AuthDbContext context)
    {
        string seed = await context.Database.SqlQueryRaw<string>(
            """
            SELECT jsonb_build_object(
                'roles', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."Roles" t),
                'permissions', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM auth."Permissions" t),
                'grants', (SELECT jsonb_agg(to_jsonb(t) ORDER BY "RoleId", "PermissionId") FROM auth."RolePermissions" t)
            )::text AS "Value"
            """).SingleAsync();
        return seed + await AccountSnapshotAsync(context);
    }

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
