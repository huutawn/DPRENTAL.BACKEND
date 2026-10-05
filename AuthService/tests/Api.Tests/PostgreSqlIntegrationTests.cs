using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Api.Exceptions;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>
/// Kiểm thử tích hợp với PostgreSQL thật:
/// - Unique constraint vi phạm ở mức DB -> được ánh xạ thành 409 Conflict.
/// - Refresh token dùng lại đồng thời -> một request thành công, request kia phát hiện reuse và thu hồi TOÀN BỘ phiên của user.
/// - Giao dịch đổi mật khẩu -> nguyên tử, cập nhật mật khẩu và thu hồi toàn bộ session.
/// - Hai admin cùng duyệt/từ chối một organization -> khóa row, chỉ quyết định đầu tiên được ghi, người sau nhận NotPendingReview.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class PostgreSqlIntegrationTests : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=5435;Database=authservice_test;Username=postgres;Password=postgres;Include Error Detail=true";

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var db = new AuthDbContext(options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new AuthDbContext(options);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostgreSql_UnapprovedIdentifiersCanRepeatButConcurrentApprovalCannot(bool duplicateSlug)
    {
        string suffix = Guid.NewGuid().ToString("N");
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        await using (var setup = CreateDbContext())
        {
            setup.Organizations.AddRange(new Organization
            {
                Id = firstId,
                Name = "First",
                Slug = $"first-{suffix}",
                TaxCode = suffix[..14],
                Status = OrganizationStatus.Pending
            }, new Organization
            {
                Id = secondId,
                Name = "Second",
                Slug = duplicateSlug ? $"first-{suffix}" : $"second-{suffix}",
                TaxCode = duplicateSlug ? null : suffix[..14],
                Status = OrganizationStatus.Rejected
            });
            await setup.SaveChangesAsync();
        }

        async Task<bool> ActivateAsync(Guid id)
        {
            await using var db = CreateDbContext();
            var repository = new EfOrganizationRepository(db);
            Organization org = (await repository.GetByIdAsync(id))!;
            org.Status = OrganizationStatus.Active;
            try
            {
                return await repository.UpdateAsync(org);
            }
            catch (StorageConstraintViolationException exception)
            {
                Assert.Equal(duplicateSlug ? StorageConstraints.OrganizationSlug : StorageConstraints.OrganizationTaxCode, exception.ConstraintName);
                return false;
            }
        }

        bool[] results = await Task.WhenAll(ActivateAsync(firstId), ActivateAsync(secondId));
        Assert.Single(results, success => success);
        Assert.Single(results, success => !success);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.Organizations.CountAsync(o => (o.Id == firstId || o.Id == secondId) && o.Status == OrganizationStatus.Active));
    }

    [Fact]
    public async Task PostgreSql_MediaOutboxClaim_IsLeasedAndCanRecoverAfterWorkerStops()
    {
        Guid messageId = Guid.NewGuid();
        await using (AuthDbContext setup = CreateDbContext())
        {
            await new EfMediaOutboxRepository(setup).AddAsync(new MediaOutboxMessage
            {
                Id = messageId,
                MediaId = Guid.NewGuid(),
                Visibility = "public",
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        await using (AuthDbContext first = CreateDbContext())
        await using (AuthDbContext second = CreateDbContext())
        {
            MediaOutboxMessage claimed = Assert.Single(await new EfMediaOutboxRepository(first).GetPendingMessagesAsync(20));
            Assert.Equal(messageId, claimed.Id);
            Assert.Equal("Processing", claimed.Status);
            Assert.Empty(await new EfMediaOutboxRepository(second).GetPendingMessagesAsync(20));

            claimed.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await first.SaveChangesAsync();
            Assert.Equal(messageId, Assert.Single(await new EfMediaOutboxRepository(second).GetPendingMessagesAsync(20)).Id);
        }

        await using AuthDbContext cleanup = CreateDbContext();
        cleanup.MediaOutboxMessages.Remove(await cleanup.MediaOutboxMessages.SingleAsync(m => m.Id == messageId));
        await cleanup.SaveChangesAsync();
    }

    [Fact]
    public async Task PostgreSql_UniqueConstraints_MappedToConflictErrors()
    {
        await using var db = CreateDbContext();

        var userRepo = new EfUserRepository(db);
        var orgRepo = new EfOrganizationRepository(db);
        var roleRepo = new EfRoleRepository(db);

        var userService = new UserService(
            userRepo, roleRepo, new PasswordHasher<User>(),
            Options.Create(new Application.Options.AdminBootstrapOptions { CreationKey = "bootstrap" }),
            TimeProvider.System, FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db), new EfAuthSessionRepository(db, TimeProvider.System),
            new EfRecoveryTokenRepository(db), FakeMediaUrlResolver.Instance);

        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        string email = $"pg_unique_{uniqueSuffix}@example.com";
        string slug = $"pg-org-{uniqueSuffix}";

        // 1. Tạo user đầu tiên thành công
        var reg1 = await userService.RegisterAsync(new RegisterRequest(email, "Password123!", "User One"));
        Assert.True(reg1.IsSuccess, reg1.Error.Message);

        // 2. Vi phạm Unique Constraint Email ở mức Postgres -> trả về 409 User.EmailAlreadyExists
        var reg2 = await userService.RegisterAsync(new RegisterRequest(email, "Password123!", "User Two"));
        Assert.True(reg2.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, reg2.Error);
        Assert.Equal(ErrorType.Conflict, reg2.Error.Type);

        // Bỏ qua bước pre-check của Service để xác nhận chính PostgreSQL phát sinh constraint đã biết.
        User duplicate = new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Duplicate",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };
        StorageConstraintViolationException violation = await Assert.ThrowsAsync<StorageConstraintViolationException>(
            () => userRepo.CreateAsync(duplicate));
        Assert.Equal(StorageConstraints.UserEmail, violation.ConstraintName);
        Assert.True(StorageConstraintMapping.TryMap(violation, out Error? mappedError));
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, mappedError);

        // 3. Hai organization cùng slug: ghi thẳng qua repository (bỏ qua pre-check) để chính Postgres chặn.
        Role? ownerRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == SystemRoleCode.Owner);
        Assert.NotNull(ownerRole);
        Organization first = NewOrganization("Acme", slug);
        first.Status = OrganizationStatus.Active;
        await orgRepo.CreateAsync(first, NewOwnerMembership(first.Id, reg1.Value.AccountId, ownerRole.Id));

        Organization second = NewOrganization("Acme Two", slug);
        second.Status = OrganizationStatus.Active;
        StorageConstraintViolationException slugViolation = await Assert.ThrowsAsync<StorageConstraintViolationException>(
            () => orgRepo.CreateAsync(second, NewOwnerMembership(second.Id, reg1.Value.AccountId, ownerRole.Id)));
        Assert.Equal(StorageConstraints.OrganizationSlug, slugViolation.ConstraintName);
        Assert.True(StorageConstraintMapping.TryMap(slugViolation, out Error? slugError));
        Assert.Equal(AuthErrors.Organization.SlugAlreadyExists, slugError);
        Assert.Equal(ErrorType.Conflict, slugError.Type);

        static Organization NewOrganization(string name, string organizationSlug) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = organizationSlug,
            Type = OrganizationType.Supplier,
            Status = OrganizationStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        static OrganizationMembership NewOwnerMembership(Guid organizationId, Guid userId, Guid roleId) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = userId,
            RoleId = roleId,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        };
    }

    [Fact]
    public async Task PostgreSql_ConcurrentRefreshToken_ReuseDetectionAndAllSessionRevocation()
    {
        await using var db = CreateDbContext();

        var sessionRepo = new EfAuthSessionRepository(db, TimeProvider.System);
        var userRepo = new EfUserRepository(db);
        var roleRepo = new EfRoleRepository(db);

        // Tạo user để tạo session
        Guid userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"refresh_race_{Guid.NewGuid():N}@example.com",
            FullName = "Refresh Race",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var issuer = new DummyAccessTokenIssuer();
        var sessionService = new AuthSessionService(
            sessionRepo, userRepo, issuer, TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db),
            new Microsoft.AspNetCore.Http.HttpContextAccessor());

        // Tạo session ban đầu
        var sessionResult = await sessionService.CreateSessionAsync(userId);
        Assert.True(sessionResult.IsSuccess);
        string refreshToken = sessionResult.Value.RefreshToken;
        Assert.True((await sessionService.CreateSessionAsync(userId)).IsSuccess);

        // BẮT ĐẦU ĐUA CONCURRENCY: 2 request cùng refresh bằng CÙNG 1 refresh token
        Task<Result<AuthTokenPair>> task1 = Task.Run(async () =>
        {
            await using var db1 = CreateDbContext();
            var repo1 = new EfAuthSessionRepository(db1, TimeProvider.System);
            var users1 = new EfUserRepository(db1);
            var s1 = new AuthSessionService(repo1, users1, issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db1),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
            return await s1.RefreshAsync(refreshToken);
        });

        Task<Result<AuthTokenPair>> task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext();
            var repo2 = new EfAuthSessionRepository(db2, TimeProvider.System);
            var users2 = new EfUserRepository(db2);
            var s2 = new AuthSessionService(repo2, users2, issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db2),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
            return await s2.RefreshAsync(refreshToken);
        });

        Result<AuthTokenPair>[] results = await Task.WhenAll(task1, task2);

        // Một trong hai request có thể thành công hoặc phát hiện reuse
        // Nhưng khi reuse được phát hiện, toàn bộ session của user BẮT BUỘC bị thu hồi
        bool anyFailedWithInvalidToken = results.Any(r => r.IsFailure && r.Error == AuthErrors.Auth.InvalidRefreshToken);
        Assert.True(anyFailedWithInvalidToken);

        await using (var verifyDb = CreateDbContext())
        {
            Assert.Empty(await verifyDb.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null).ToListAsync());
        }

        // Nếu một request đã kích hoạt reuse detection -> thử dùng lại refresh token cũ chắc chắn thất bại
        var retryOld = await sessionService.RefreshAsync(refreshToken);
        Assert.True(retryOld.IsFailure);
        Assert.Equal(AuthErrors.Auth.InvalidRefreshToken, retryOld.Error);
    }

    [Fact]
    public async Task PostgreSql_PasswordChange_Transaction_UpdatesPasswordAndRevokesSessions()
    {
        await using var db = CreateDbContext();

        var userRepo = new EfUserRepository(db);
        var sessionRepo = new EfAuthSessionRepository(db, TimeProvider.System);
        var roleRepo = new EfRoleRepository(db);

        Guid userId = Guid.NewGuid();
        var hasher = new PasswordHasher<User>();
        User user = new()
        {
            Id = userId,
            Email = $"pw_tx_{Guid.NewGuid():N}@example.com",
            FullName = "PW Tx",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, "OldPassword123!");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var sessionService = new AuthSessionService(
            sessionRepo, userRepo, new DummyAccessTokenIssuer(), TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db),
            new Microsoft.AspNetCore.Http.HttpContextAccessor());

        // Tạo 2 session cho user
        var s1 = await sessionService.CreateSessionAsync(userId);
        var s2 = await sessionService.CreateSessionAsync(userId);
        Assert.True(s1.IsSuccess && s2.IsSuccess);

        var userService = new UserService(
            userRepo, roleRepo, hasher,
            Options.Create(new Application.Options.AdminBootstrapOptions { CreationKey = "bootstrap" }),
            TimeProvider.System, FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db), sessionRepo, new EfRecoveryTokenRepository(db),
            FakeMediaUrlResolver.Instance);

        // Đổi mật khẩu
        var changeResult = await userService.ChangePasswordAsync(userId, new ChangePasswordRequest("OldPassword123!", "NewPassword123!"));
        Assert.True(changeResult.IsSuccess);

        // Kiểm tra trong PostgreSQL:
        await using var verifyDb = CreateDbContext();
        User? reloadedUser = await verifyDb.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(reloadedUser, reloadedUser.PasswordHash, "NewPassword123!"));

        // Toàn bộ session cũ của user phải bị thu hồi
        List<Session> activeSessions = await verifyDb.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null).ToListAsync();
        Assert.Empty(activeSessions);
    }

    [Fact]
    public async Task PostgreSql_ConcurrentApproveAndReject_OnlyFirstDecisionWins()
    {
        await using var db = CreateDbContext();

        Permission suspend = await db.Permissions.SingleAsync(p => p.Code == PermissionCode.IdentityOrganizationsSuspend);
        Role reviewerRole = new() { Id = Guid.NewGuid(), Name = $"reviewer_{Guid.NewGuid():N}", Scope = RoleScope.Global };
        db.Roles.Add(reviewerRole);
        db.RolePermissions.Add(new RolePermission { RoleId = reviewerRole.Id, PermissionId = suspend.Id });

        Guid adminAId = Guid.NewGuid();
        Guid adminBId = Guid.NewGuid();
        foreach (Guid adminId in new[] { adminAId, adminBId })
        {
            db.Users.Add(new User { Id = adminId, Email = $"reviewer_{adminId:N}@example.com", FullName = "Reviewer", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow });
            db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = reviewerRole.Id });
        }

        Guid orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Pending Review Org",
            Slug = $"pending-review-{Guid.NewGuid():N}",
            Status = OrganizationStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        static OrganizationReviewService CreateReviewService(AuthDbContext context) => new(
            new EfOrganizationRepository(context),
            new EfOrganizationMembershipRepository(context),
            new EfRoleRepository(context),
            new EfAuditLogRepository(context),
            TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            FakeMediaUrlResolver.Instance);

        // Hai admin cùng mở hàng chờ và bấm cùng lúc: một người duyệt, một người từ chối.
        Task<Result<OrganizationResponse>> approve = Task.Run(async () =>
        {
            await using var dbA = CreateDbContext();
            return await CreateReviewService(dbA).ApproveAsync(adminAId, orgId);
        });
        Task<Result<OrganizationResponse>> reject = Task.Run(async () =>
        {
            await using var dbB = CreateDbContext();
            return await CreateReviewService(dbB).RejectAsync(adminBId, orgId, new RejectOrganizationRequest("Thiếu giấy phép"));
        });

        Result<OrganizationResponse>[] results = await Task.WhenAll(approve, reject);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error == AuthErrors.Organization.NotPendingReview);

        // Trạng thái trong DB phải khớp đúng người thắng — không có chuyện "duyệt rồi bị ghi đè thành từ chối".
        OrganizationResponse winner = results.Single(r => r.IsSuccess).Value;
        await using var verifyDb = CreateDbContext();
        Organization stored = await verifyDb.Organizations.SingleAsync(o => o.Id == orgId);
        Assert.Equal(winner.Status, stored.Status);
        Assert.Equal(stored.Status == OrganizationStatus.Active ? adminAId : adminBId, stored.ReviewedByUserId);
    }

    [Fact]
    public async Task PostgreSql_PagedSearchTranslatesForAllRepositories()
    {
        await using AuthDbContext db = CreateDbContext();
        string suffix = Guid.NewGuid().ToString("N");
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = $"search-{suffix}@example.com",
            FullName = $"Search {suffix}",
            PasswordHash = "hash"
        };
        Role role = new() { Id = Guid.NewGuid(), Name = $"SearchRole{suffix}", Scope = RoleScope.Organization };
        Organization organization = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Search Org {suffix}",
            Slug = $"search-org-{suffix}",
            Status = OrganizationStatus.Active
        };
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            User = user,
            Organization = organization,
            Role = role,
            Status = MembershipStatus.Active
        });
        await db.SaveChangesAsync();

        string term = suffix[..12];
        PageRequest request = new(Search: $" {term.ToUpperInvariant()} ");
        PagedResult<Organization> all = await new EfOrganizationRepository(db).GetAllAsync(request);
        var (users, userCount) = await new EfUserRepository(db).GetUsersPagedAsync(0, 20, request.GetSearchTerm());

        Assert.Equal(organization.Id, Assert.Single(all.Items).Id);
        Assert.Equal(1, all.TotalCount);
        Assert.Equal(user.Id, Assert.Single(users).Id);
        Assert.Equal(1, userCount);
    }

    [Fact]
    public async Task PostgreSql_Organization_ConcurrentSuspend_DoesNotOverwriteStateAndWritesSingleAuditLog()
    {
        await using var db = CreateDbContext();

        Permission suspend = await db.Permissions.SingleAsync(p => p.Code == PermissionCode.IdentityOrganizationsSuspend);
        Role adminRole = new() { Id = Guid.NewGuid(), Name = $"admin_suspend_{Guid.NewGuid():N}", Scope = RoleScope.Global };
        db.Roles.Add(adminRole);
        db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = suspend.Id });

        Guid adminAId = Guid.NewGuid();
        Guid adminBId = Guid.NewGuid();
        foreach (Guid adminId in new[] { adminAId, adminBId })
        {
            db.Users.Add(new User { Id = adminId, Email = $"admin_{adminId:N}@example.com", FullName = "Admin", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow });
            db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });
        }

        Guid orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Active Org For Concurrency",
            Slug = $"active-concurrent-{Guid.NewGuid():N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        static OrganizationService CreateService(AuthDbContext context) => new(
            new EfOrganizationRepository(context),
            new EfOrganizationMembershipRepository(context),
            new EfRoleRepository(context),
            FakeMediaUrlResolver.Instance,
            new EfAuditLogRepository(context),
            FixedCorrelationIdProvider.Instance,
            new FakeMediaServiceClient(),
            new FakeMediaOutboxService());

        Task<Result> taskA = Task.Run(async () =>
        {
            await using var dbA = CreateDbContext();
            return await CreateService(dbA).SuspendAsync(adminAId, orgId, new SuspendOrganizationRequest("Concurrent suspend A"));
        });
        Task<Result> taskB = Task.Run(async () =>
        {
            await using var dbB = CreateDbContext();
            return await CreateService(dbB).SuspendAsync(adminBId, orgId, new SuspendOrganizationRequest("Concurrent suspend B"));
        });

        Result[] results = await Task.WhenAll(taskA, taskB);
        Assert.All(results, r => Assert.True(r.IsSuccess));

        await using var verifyDb = CreateDbContext();
        Organization stored = await verifyDb.Organizations.SingleAsync(o => o.Id == orgId);
        Assert.Equal(OrganizationStatus.Suspended, stored.Status);

        var auditLogs = await verifyDb.AuditLogs.Where(l => l.Target == orgId.ToString() && l.Action == "Organization_Suspend").ToListAsync();
        Assert.Single(auditLogs);
    }

    [Fact]
    public async Task PostgreSql_Organization_AuditFailure_RollsBackProfileOutboxAndSuspend()
    {
        await using var db = CreateDbContext();

        Permission update = await db.Permissions.SingleAsync(p => p.Code == PermissionCode.IdentityOrganizationsUpdate);
        Permission suspend = await db.Permissions.SingleAsync(p => p.Code == PermissionCode.IdentityOrganizationsSuspend);
        Role adminRole = new() { Id = Guid.NewGuid(), Name = $"admin_tx_{Guid.NewGuid():N}", Scope = RoleScope.Global };
        db.Roles.Add(adminRole);
        db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = update.Id });
        db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = suspend.Id });

        Guid adminId = Guid.NewGuid();
        db.Users.Add(new User { Id = adminId, Email = $"admin_tx_{adminId:N}@example.com", FullName = "Admin Tx", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });

        Guid orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Original Unchanged Name",
            Slug = $"tx-test-{Guid.NewGuid():N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var throwingAuditRepo = new ThrowingAuditLogRepository();
        Guid logoId = Guid.NewGuid();
        var mediaClient = new FakeMediaServiceClient { AutoVerify = true, DefaultUserId = adminId };
        var service = new OrganizationService(
            new EfOrganizationRepository(db),
            new EfOrganizationMembershipRepository(db),
            new EfRoleRepository(db),
            FakeMediaUrlResolver.Instance,
            throwingAuditRepo,
            FixedCorrelationIdProvider.Instance,
            mediaClient,
            new MediaOutboxService(new EfMediaOutboxRepository(db), mediaClient, TimeProvider.System, NullLogger<MediaOutboxService>.Instance));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateProfileAsync(adminId, orgId, new UpdateOrganizationRequest("Should Rollback Name", "rollback@example.com", "0901234567", "123 Street", "Hà Nội", "Phường 1", LogoMediaId: logoId)));

        await using (var verifyDb = CreateDbContext())
        {
            Organization org = await verifyDb.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal("Original Unchanged Name", org.Name);
            Assert.Null(org.LogoMediaId);
            Assert.False(await verifyDb.MediaOutboxMessages.AnyAsync(m => m.MediaId == logoId));
            Assert.False(await verifyDb.AuditLogs.AnyAsync(l => l.Target == orgId.ToString()));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SuspendAsync(adminId, orgId, new SuspendOrganizationRequest("Rollback reason")));

        await using (var verifyDb = CreateDbContext())
        {
            Organization org = await verifyDb.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal(OrganizationStatus.Active, org.Status);
        }
    }

    [Fact]
    public async Task PostgreSql_Organization_ConcurrentProfileUpdates_AreSerializedByRowLock()
    {
        await using var setup = CreateDbContext();
        Guid orgId = Guid.NewGuid();
        Guid adminA = Guid.NewGuid();
        Guid adminB = Guid.NewGuid();

        setup.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Concurrent Name",
            Slug = $"conc-{Guid.NewGuid():N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        });

        Permission updatePerm = await setup.Permissions.SingleAsync(p => p.Code == PermissionCode.IdentityOrganizationsUpdate);
        Role adminRole = new() { Id = Guid.NewGuid(), Name = $"admin_conc_{Guid.NewGuid():N}", Scope = RoleScope.Global };
        setup.Roles.Add(adminRole);
        setup.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = updatePerm.Id });

        setup.Users.AddRange(
            new User { Id = adminA, Email = $"adminA_{adminA:N}@test.com", FullName = "Admin A", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow },
            new User { Id = adminB, Email = $"adminB_{adminB:N}@test.com", FullName = "Admin B", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow }
        );
        setup.UserRoles.AddRange(
            new UserRole { UserId = adminA, RoleId = adminRole.Id },
            new UserRole { UserId = adminB, RoleId = adminRole.Id }
        );
        await setup.SaveChangesAsync();

        Task<Result> taskA = Task.Run(async () =>
        {
            await using var dbA = CreateDbContext();
            var service = new OrganizationService(
                new EfOrganizationRepository(dbA),
                new EfOrganizationMembershipRepository(dbA),
                new EfRoleRepository(dbA),
                FakeMediaUrlResolver.Instance,
                new EfAuditLogRepository(dbA),
                FixedCorrelationIdProvider.Instance,
                new FakeMediaServiceClient(),
                new FakeMediaOutboxService());
            return await service.UpdateProfileAsync(adminA, orgId, new UpdateOrganizationRequest("Name From A", "a@test.com", "0901234567", "Addr A", "HN", "W1"));
        });

        Task<Result> taskB = Task.Run(async () =>
        {
            await using var dbB = CreateDbContext();
            var service = new OrganizationService(
                new EfOrganizationRepository(dbB),
                new EfOrganizationMembershipRepository(dbB),
                new EfRoleRepository(dbB),
                FakeMediaUrlResolver.Instance,
                new EfAuditLogRepository(dbB),
                FixedCorrelationIdProvider.Instance,
                new FakeMediaServiceClient(),
                new FakeMediaOutboxService());
            return await service.UpdateProfileAsync(adminB, orgId, new UpdateOrganizationRequest("Name From B", "b@test.com", "0901234567", "Addr B", "HN", "W2"));
        });

        Result[] results = await Task.WhenAll(taskA, taskB);
        Assert.All(results, r => Assert.True(r.IsSuccess));

        await using var verifyDb = CreateDbContext();
        Organization stored = await verifyDb.Organizations.SingleAsync(o => o.Id == orgId);
        Assert.True(stored.Name is "Name From A" or "Name From B");

        var auditLogs = await verifyDb.AuditLogs.Where(l => l.Target == orgId.ToString() && l.Action == "Organization_ProfileUpdate").ToListAsync();
        Assert.Equal(2, auditLogs.Count);
    }

    [Fact]
    public async Task PostgreSql_Organization_AuditFailure_RollsBackRegistration()
    {
        await using var db = CreateDbContext();
        Guid userId = Guid.NewGuid();
        Role ownerRole = await db.Roles.SingleAsync(r => r.Name == "Owner");

        db.Users.Add(new User
        {
            Id = userId,
            Email = $"reg_{userId:N}@example.com",
            FullName = "Reg User",
            PasswordHash = "h",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CitizenId = Random.Shared.NextInt64(100000000000L, 999999999999L).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProfileCompletedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var throwingAudit = new ThrowingAuditLogRepository();
        var regService = new OrganizationRegistrationService(
            new EfOrganizationRepository(db),
            new EfOrganizationMembershipRepository(db),
            new EfUserRepository(db),
            new EfRoleRepository(db),
            throwingAudit,
            TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            new FakeMediaServiceClient { AutoVerify = true, DefaultUserId = userId },
            FakeMediaUrlResolver.Instance,
            new FakeMediaOutboxService());

        string slug = $"fail-reg-{Guid.NewGuid():N}";
        OrganizationProfileRequest request = new(
            "Fail Reg Corp",
            OrganizationType.Supplier,
            Random.Shared.Next(1000000000, 1999999999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Nguyen Van Reg",
            $"reg_{userId:N}@example.com",
            "0901234567",
            "123 Street",
            "Hà Nội",
            "Phường 1",
            BusinessLicenseDocumentMediaId: Guid.NewGuid(),
            Slug: slug);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            regService.RegisterForExistingUserAsync(userId, request));

        await using var verifyDb = CreateDbContext();
        Assert.Null(await verifyDb.Organizations.SingleOrDefaultAsync(o => o.Slug == slug));
    }

    [Fact]
    public async Task PostgreSql_EfAuditLogRepository_SearchAndFiltering_SpecialCharactersAndDiacriticsAndStatus()
    {
        await using var db = CreateDbContext();
        Guid orgId = Guid.NewGuid();
        Guid otherOrgId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();

        db.Organizations.AddRange(
            new Organization
            {
                Id = orgId,
                Name = "Postgres Hist Org",
                Slug = $"pg-hist-{Guid.NewGuid():N}",
                Status = OrganizationStatus.Active,
                CreatedAtUtc = DateTime.UtcNow
            },
            new Organization
            {
                Id = otherOrgId,
                Name = "Other Org",
                Slug = $"pg-other-{Guid.NewGuid():N}",
                Status = OrganizationStatus.Active,
                CreatedAtUtc = DateTime.UtcNow
            }
        );

        DateTimeOffset now = DateTimeOffset.UtcNow;
        AuditLog log1 = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = orgId.ToString(),
            Action = "Organization_Suspend",
            Status = OrganizationStatus.Suspended,
            Result = "Success",
            Reason = @"Giấy phép 100%_hết\hạn",
            Timestamp = now.AddMinutes(10)
        };
        AuditLog log2 = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = orgId.ToString(),
            Action = "Organization_Activate",
            Status = OrganizationStatus.Active,
            Result = "Success",
            Reason = "Đã bổ sung giấy phép",
            Timestamp = now.AddMinutes(20)
        };
        AuditLog log3 = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = orgId.ToString(),
            Action = "Organization_Register",
            Status = OrganizationStatus.Pending,
            Result = "Success",
            Reason = "Đăng ký 50%",
            Timestamp = now.AddMinutes(30)
        };
        AuditLog log4 = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = orgId.ToString(),
            Action = "Organization_ProfileUpdate",
            Status = null,
            Result = "Success",
            Reason = null,
            Timestamp = now.AddMinutes(40)
        };
        AuditLog logOtherOrg = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = otherOrgId.ToString(),
            Action = "Organization_Suspend",
            Status = OrganizationStatus.Suspended,
            Result = "Success",
            Reason = @"Giấy phép 100%_hết\hạn",
            Timestamp = now.AddMinutes(50)
        };
        AuditLog logNonOrgAction = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = orgId.ToString(),
            Action = "User_PasswordChange",
            Status = null,
            Result = "Success",
            Reason = "100%",
            Timestamp = now.AddMinutes(60)
        };

        db.AuditLogs.AddRange(log1, log2, log3, log4, logOtherOrg, logNonOrgAction);
        await db.SaveChangesAsync();

        EfAuditLogRepository repo = new(db);

        // 1. Literal '%' search: matches log1 and log3 (100% and 50%), neither other org nor all logs
        var pctResult = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Search: "%"));
        Assert.Equal(2, pctResult.TotalCount);
        Assert.Contains(pctResult.Items, l => l.Id == log1.Id);
        Assert.Contains(pctResult.Items, l => l.Id == log3.Id);

        // 2. Reason filter with '_': matches only log1 (not non-matching or wildcards)
        var underscoreResult = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(), reason: "_");
        Assert.Equal(1, underscoreResult.TotalCount);
        Assert.Equal(log1.Id, underscoreResult.Items[0].Id);

        // 3. Search with '\': matches only log1
        var backslashResult = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Search: @"\"));
        Assert.Equal(1, backslashResult.TotalCount);
        Assert.Equal(log1.Id, backslashResult.Items[0].Id);

        // 4. Vietnamese diacritics substring search: matches log1 and log2
        var vnResult = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Search: "giấy phép"));
        Assert.Equal(2, vnResult.TotalCount);
        Assert.Contains(vnResult.Items, l => l.Id == log1.Id);
        Assert.Contains(vnResult.Items, l => l.Id == log2.Id);

        // 5. Search by Status enum name: "Pending" matches log3
        var statusSearch = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Search: "Pending"));
        Assert.Equal(1, statusSearch.TotalCount);
        Assert.Equal(log3.Id, statusSearch.Items[0].Id);

        // 6. Filter by Status exact: Suspended -> log1; null status (log4) is NOT matched
        var statusFilter = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(), status: OrganizationStatus.Suspended);
        Assert.Equal(1, statusFilter.TotalCount);
        Assert.Equal(log1.Id, statusFilter.Items[0].Id);

        // 7. Filter by action case-insensitive: "organization_suspend" -> log1
        var actionFilter = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(), action: "organization_suspend");
        Assert.Equal(1, actionFilter.TotalCount);
        Assert.Equal(log1.Id, actionFilter.Items[0].Id);

        // 8. Filter by result case-insensitive: "SUCCESS" -> all 4 org logs
        var resultFilter = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(), result: "SUCCESS");
        Assert.Equal(4, resultFilter.TotalCount);

        // 9. Combined AND: action + status + result + reason
        var andFilter = await repo.GetOrganizationHistoryAsync(
            orgId,
            new PageRequest(),
            action: "Organization_Suspend",
            status: OrganizationStatus.Suspended,
            result: "Success",
            reason: "100%");
        Assert.Equal(1, andFilter.TotalCount);
        Assert.Equal(log1.Id, andFilter.Items[0].Id);

        // 10. Isolation: verify no logs from other org or non-Organization_ actions appear
        var allLogs = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(PageSize: 100));
        Assert.Equal(4, allLogs.TotalCount);
        Assert.DoesNotContain(allLogs.Items, l => l.Target != orgId.ToString());
        Assert.DoesNotContain(allLogs.Items, l => !l.Action.StartsWith("Organization_", StringComparison.Ordinal));

        // 11. Pagination & Ordering: newest first (log4 > log3 > log2 > log1)
        var page1 = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Page: 1, PageSize: 2));
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(log4.Id, page1.Items[0].Id);
        Assert.Equal(log3.Id, page1.Items[1].Id);

        var page2 = await repo.GetOrganizationHistoryAsync(orgId, new PageRequest(Page: 2, PageSize: 2));
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal(log2.Id, page2.Items[0].Id);
        Assert.Equal(log1.Id, page2.Items[1].Id);
    }

    private sealed class ThrowingAuditLogRepository : IAuditLogRepository
    {
        public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated audit write failure for transaction rollback test.");

        public Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(Guid organizationId, PageRequest request, string? action = null, OrganizationStatus? status = null, string? result = null, string? reason = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated audit read failure for transaction rollback test.");
    }

    private sealed class DummyAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) =>
            new("dummy-access-token", DateTimeOffset.UtcNow.AddMinutes(15));
    }
}
