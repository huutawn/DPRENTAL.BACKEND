using System.Diagnostics;
using Npgsql;
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
/// - Hai yêu cầu cùng thay đổi Owner cuối cùng -> khóa organization theo thứ tự cố định, chỉ 1 request thành công, request kia bị chặn với LastOwner.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class PostgreSqlIntegrationTests : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=5435;Database=authservice_test;Username=postgres;Password=postgres;Include Error Detail=true";

    public async Task InitializeAsync()
    {
        await EnsurePostgresContainerRunningAsync();

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

    private static async Task EnsurePostgresContainerRunningAsync()
    {
        if (await CanConnectAsync())
        {
            return;
        }

        ProcessStartInfo psi = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in new[] { "run", "--rm", "-d", "--name", "authservice-pg-test", "-e", "POSTGRES_PASSWORD", "-e", "POSTGRES_DB", "-p", "5435:5432", "postgres:17-alpine" })
        {
            psi.ArgumentList.Add(argument);
        }
        psi.Environment["POSTGRES_PASSWORD"] = "postgres";
        psi.Environment["POSTGRES_DB"] = "authservice_test";

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Không thể khởi động Docker để chạy PostgreSQL integration tests.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Không thể khởi động PostgreSQL test container: {error}");
        }

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (await CanConnectAsync())
            {
                return;
            }
            await Task.Delay(500);
        }

        throw new TimeoutException("PostgreSQL test container chưa sẵn sàng sau 30 giây.");
    }

    private static async Task<bool> CanConnectAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await connection.OpenAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task PostgreSql_UniqueConstraints_MappedToConflictErrors()
    {
        await using var db = CreateDbContext();

        var userRepo = new EfUserRepository(db);
        var orgRepo = new EfOrganizationRepository(db);
        var memRepo = new EfOrganizationMembershipRepository(db);
        var roleRepo = new EfRoleRepository(db);

        var userService = new UserService(
            userRepo, roleRepo, new PasswordHasher<User>(),
            Options.Create(new Application.Options.AdminBootstrapOptions { CreationKey = "bootstrap" }),
            TimeProvider.System, FixedCorrelationIdProvider.Instance,
            new EfAuditLogRepository(db), new EfAuthSessionRepository(db, TimeProvider.System),
            new EfRecoveryTokenRepository(db), NullLogger<UserService>.Instance);

        var orgService = new OrganizationService(
            orgRepo, memRepo, userRepo, roleRepo,
            FixedCorrelationIdProvider.Instance,
            NullLogger<OrganizationService>.Instance);

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

        // Cấp quyền IdentityOrganizationsCreate cho user để có thể tạo Organization
        Role? adminPlatformRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "admin_platform");
        if (adminPlatformRole is not null)
        {
            db.UserRoles.Add(new UserRole { UserId = reg1.Value.AccountId, RoleId = adminPlatformRole.Id });
            await db.SaveChangesAsync();
        }

        // 3. Tạo Organization đầu tiên thành công
        var createOrg1 = await orgService.CreateAsync(reg1.Value.AccountId, new CreateOrganizationRequest("Acme", slug, OrganizationType.Supplier));
        Assert.True(createOrg1.IsSuccess, createOrg1.Error.Message);

        // 4. Vi phạm Unique Constraint Slug ở mức Postgres -> trả về 409 Organization.SlugAlreadyExists
        var createOrg2 = await orgService.CreateAsync(reg1.Value.AccountId, new CreateOrganizationRequest("Acme Two", slug, OrganizationType.Supplier));
        Assert.True(createOrg2.IsFailure);
        Assert.Equal(AuthErrors.Organization.SlugAlreadyExists, createOrg2.Error);
        Assert.Equal(ErrorType.Conflict, createOrg2.Error.Type);
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
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance,
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
                new EfAuditLogRepository(db1), NullLogger<AuthSessionService>.Instance,
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
                new EfAuditLogRepository(db2), NullLogger<AuthSessionService>.Instance,
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
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance,
            new Microsoft.AspNetCore.Http.HttpContextAccessor());

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
                new EfAuditLogRepository(db1), NullLogger<AuthSessionService>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
            return await s1.ExchangeGoogleLoginTicketAsync(ticket);
        });

        Task<Result<AuthTokenPair>> task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext();
            var s2 = new AuthSessionService(
                new EfAuthSessionRepository(db2, TimeProvider.System),
                new EfUserRepository(db2), issuer, TimeProvider.System,
                FixedCorrelationIdProvider.Instance,
                new EfAuditLogRepository(db2), NullLogger<AuthSessionService>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
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
                new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
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
                new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor());
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
            new EfAuditLogRepository(db), NullLogger<AuthSessionService>.Instance,
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
    public async Task PostgreSql_ConcurrentOwnerChange_DeterministicLockingPreventsZeroOwners()
    {
        await using var db = CreateDbContext();

        var orgRepo = new EfOrganizationRepository(db);
        var memRepo = new EfOrganizationMembershipRepository(db);
        var userRepo = new EfUserRepository(db);
        var roleRepo = new EfRoleRepository(db);

        // Tạo 2 Owner và 1 Organization
        Guid orgId = Guid.NewGuid();
        Guid ownerAId = Guid.NewGuid();
        Guid ownerBId = Guid.NewGuid();

        Role? ownerRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Owner");
        if (ownerRole is null)
        {
            ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner", Scope = RoleScope.Organization };
            db.Roles.Add(ownerRole);
        }

        Organization org = new()
        {
            Id = orgId,
            Name = "Two Owners Org",
            Slug = $"two-owners-{Guid.NewGuid():N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Organizations.Add(org);

        User userA = new() { Id = ownerAId, Email = $"owner_a_{Guid.NewGuid():N}@example.com", FullName = "Owner A", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow };
        User userB = new() { Id = ownerBId, Email = $"owner_b_{Guid.NewGuid():N}@example.com", FullName = "Owner B", PasswordHash = "h", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(userA, userB);

        OrganizationMembership memA = new() { Id = Guid.NewGuid(), OrganizationId = orgId, UserId = ownerAId, RoleId = ownerRole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow };
        OrganizationMembership memB = new() { Id = Guid.NewGuid(), OrganizationId = orgId, UserId = ownerBId, RoleId = ownerRole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow };
        db.OrganizationMemberships.AddRange(memA, memB);
        await db.SaveChangesAsync();

        // 2 request cùng lúc cố gắng xóa / rời khỏi org (cả 2 đều là Owner)
        // Nhờ LockOrganizationsAsync (SELECT ... FOR UPDATE), một trong hai request sẽ được serialize
        Task<Result> taskA = Task.Run(async () =>
        {
            await using var dbA = CreateDbContext();
            var serviceA = new OrganizationService(
                new EfOrganizationRepository(dbA),
                new EfOrganizationMembershipRepository(dbA),
                new EfUserRepository(dbA),
                new EfRoleRepository(dbA),
                FixedCorrelationIdProvider.Instance,
                NullLogger<OrganizationService>.Instance);
            return await serviceA.LeaveAsync(ownerAId, orgId);
        });

        Task<Result> taskB = Task.Run(async () =>
        {
            await using var dbB = CreateDbContext();
            var serviceB = new OrganizationService(
                new EfOrganizationRepository(dbB),
                new EfOrganizationMembershipRepository(dbB),
                new EfUserRepository(dbB),
                new EfRoleRepository(dbB),
                FixedCorrelationIdProvider.Instance,
                NullLogger<OrganizationService>.Instance);
            return await serviceB.LeaveAsync(ownerBId, orgId);
        });

        Result[] results = await Task.WhenAll(taskA, taskB);

        int successCount = results.Count(r => r.IsSuccess);
        int lastOwnerCount = results.Count(r => r.IsFailure && r.Error == AuthErrors.Organization.LastOwner);
        Assert.Equal(1, successCount);
        Assert.Equal(1, lastOwnerCount);

        // Kiểm tra trong PostgreSQL: tổ chức BẮT BUỘC còn đúng 1 Owner, KHÔNG BAO GIỜ bị 0 Owner!
        await using var verifyDb = CreateDbContext();
        int remainingOwners = await verifyDb.OrganizationMemberships
            .CountAsync(m => m.OrganizationId == orgId && m.RoleId == ownerRole.Id && m.Status == MembershipStatus.Active);

        Assert.Equal(1, remainingOwners);
    }

    private sealed class DummyAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) =>
            new("dummy-access-token", DateTimeOffset.UtcNow.AddMinutes(15));
    }
}
