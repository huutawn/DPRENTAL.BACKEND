using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
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
/// - Ticket dùng 1 lần (Google login ticket) đồng thời -> đúng 1 request thành công.
/// - Giao dịch đổi mật khẩu -> nguyên tử, cập nhật mật khẩu và thu hồi toàn bộ session.
/// - Liên kết Google vào tài khoản có mật khẩu -> liên kết lỗi thì mật khẩu và session giữ nguyên (rollback).
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
            new EfRecoveryTokenRepository(db), NullLogger<UserService>.Instance);

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
        await orgRepo.CreateAsync(first, NewOwnerMembership(first.Id, reg1.Value.AccountId, ownerRole.Id));

        Organization second = NewOrganization("Acme Two", slug);
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
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance);

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
                new EfAuditLogRepository(db1), NullLogger<AuthSessionService>.Instance);
            return await s1.RefreshAsync(refreshToken);
        });

        Task<Result<AuthTokenPair>> task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext();
            var repo2 = new EfAuthSessionRepository(db2, TimeProvider.System);
            var users2 = new EfUserRepository(db2);
            var s2 = new AuthSessionService(repo2, users2, issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db2), NullLogger<AuthSessionService>.Instance);
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
    public async Task PostgreSql_OneTimeGoogleTicket_Concurrency()
    {
        await using var db = CreateDbContext();

        var sessionRepo = new EfAuthSessionRepository(db, TimeProvider.System);
        var userRepo = new EfUserRepository(db);

        Guid userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"ticket_race_{Guid.NewGuid():N}@example.com",
            FullName = "Ticket Race",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var issuer = new DummyAccessTokenIssuer();
        var sessionService = new AuthSessionService(
            sessionRepo, userRepo, issuer, TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance);

        var ticketResult = await sessionService.CreateGoogleLoginTicketAsync(userId);
        Assert.True(ticketResult.IsSuccess);
        string ticket = ticketResult.Value;

        // ĐUA CONCURRENCY: 2 request cùng exchange 1 ticket ngắn hạn
        Task<Result<AuthTokenPair>> task1 = Task.Run(async () =>
        {
            await using var db1 = CreateDbContext();
            var s1 = new AuthSessionService(
                new EfAuthSessionRepository(db1, TimeProvider.System),
                new EfUserRepository(db1), issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db1), NullLogger<AuthSessionService>.Instance);
            return await s1.ExchangeGoogleLoginTicketAsync(ticket);
        });

        Task<Result<AuthTokenPair>> task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext();
            var s2 = new AuthSessionService(
                new EfAuthSessionRepository(db2, TimeProvider.System),
                new EfUserRepository(db2), issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db2), NullLogger<AuthSessionService>.Instance);
            return await s2.ExchangeGoogleLoginTicketAsync(ticket);
        });

        Result<AuthTokenPair>[] results = await Task.WhenAll(task1, task2);

        // ĐÚNG 1 request được thành công, request kia phải thất bại vì ticket dùng 1 lần
        int successCount = results.Count(r => r.IsSuccess);
        int failureCount = results.Count(r => r.IsFailure);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);
    }

    [Fact]
    public async Task PostgreSql_GoogleTicket_ForInactiveUser_DoesNotCreateSession()
    {
        Guid userId = Guid.NewGuid();
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = $"inactive_ticket_{Guid.NewGuid():N}@example.com",
                FullName = "Inactive Ticket",
                PasswordHash = "hash",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        string ticket;
        await using (var db = CreateDbContext())
        {
            var service = new AuthSessionService(
                new EfAuthSessionRepository(db, TimeProvider.System),
                new EfUserRepository(db), new DummyAccessTokenIssuer(), TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance);
            var result = await service.CreateGoogleLoginTicketAsync(userId);
            Assert.True(result.IsSuccess);
            ticket = result.Value;
        }

        await using (var db = CreateDbContext())
        {
            await db.Users.Where(user => user.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        }

        await using (var db = CreateDbContext())
        {
            var service = new AuthSessionService(
                new EfAuthSessionRepository(db, TimeProvider.System),
                new EfUserRepository(db), new DummyAccessTokenIssuer(), TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance);
            var result = await service.ExchangeGoogleLoginTicketAsync(ticket);
            Assert.True(result.IsFailure);
            Assert.Equal("Auth.InvalidGoogleLoginTicket", result.Error.Code);
            Assert.False(await db.Sessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null));
        }
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
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance);

        // Tạo 2 session cho user
        var s1 = await sessionService.CreateSessionAsync(userId);
        var s2 = await sessionService.CreateSessionAsync(userId);
        Assert.True(s1.IsSuccess && s2.IsSuccess);

        var userService = new UserService(
            userRepo, roleRepo, hasher,
            Options.Create(new Application.Options.AdminBootstrapOptions { CreationKey = "bootstrap" }),
            TimeProvider.System, FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db), sessionRepo, new EfRecoveryTokenRepository(db),
            NullLogger<UserService>.Instance);

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
    public async Task PostgreSql_LinkWithPasswordInvalidation_RollsBackWhenLinkViolatesUniqueConstraint()
    {
        await using var db = CreateDbContext();
        DateTime utcNow = DateTime.UtcNow;
        string subject = $"sub_{Guid.NewGuid():N}";

        // Subject Google này đã thuộc về một user khác -> thêm liên kết sẽ vi phạm unique (Provider, ProviderSubject).
        User other = new() { Id = Guid.NewGuid(), Email = $"link_other_{Guid.NewGuid():N}@example.com", FullName = "Other", PasswordHash = "x", IsActive = true, CreatedAt = utcNow };
        other.ExternalLogins.Add(new ExternalLogin { Id = Guid.NewGuid(), UserId = other.Id, Provider = "Google", ProviderSubject = subject, CreatedAtUtc = utcNow });
        User target = new() { Id = Guid.NewGuid(), Email = $"link_target_{Guid.NewGuid():N}@example.com", FullName = "Target", PasswordHash = "original-hash", HasPassword = true, IsActive = true, CreatedAt = utcNow };
        Session session = new() { Id = Guid.NewGuid(), UserId = target.Id, CreatedAtUtc = utcNow, ExpiresAtUtc = utcNow.AddDays(7) };
        db.Users.AddRange(other, target);
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var repository = new EfExternalLoginRepository(db);
        ExternalLogin conflicting = new() { Id = Guid.NewGuid(), UserId = target.Id, Provider = "Google", ProviderSubject = subject, CreatedAtUtc = utcNow };

        StorageConstraintViolationException ex = await Assert.ThrowsAsync<StorageConstraintViolationException>(
            () => repository.AddAndInvalidatePasswordAsync(conflicting, "unusable-hash", utcNow));
        Assert.Equal(StorageConstraintViolationType.Unique, ex.ViolationType);

        // Không được xoá mật khẩu hay thu hồi phiên khi liên kết thất bại.
        await using var verifyDb = CreateDbContext();
        User reloaded = await verifyDb.Users.SingleAsync(u => u.Id == target.Id);
        Assert.Equal("original-hash", reloaded.PasswordHash);
        Assert.True(reloaded.HasPassword);
        Assert.Null((await verifyDb.Sessions.SingleAsync(s => s.Id == session.Id)).RevokedAtUtc);
        Assert.False(await verifyDb.ExternalLogins.AnyAsync(l => l.UserId == target.Id));
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
            NullLogger<OrganizationReviewService>.Instance);

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

    private sealed class DummyAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) =>
            new("dummy-access-token", DateTimeOffset.UtcNow.AddMinutes(15));
    }
}
