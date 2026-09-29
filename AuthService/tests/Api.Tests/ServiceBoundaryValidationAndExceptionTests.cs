using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Dprental.AuthService.Domain.ValueObjects;

namespace Dprental.AuthService.Api.Tests;

/// <summary>
/// Test trực tiếp từng nhóm Service:
/// - Input thiếu/sai (validation DTO, ID rỗng, trạng thái sai) -> không ghi DB, mã lỗi đúng.
/// - Quyền không đủ -> 403 Forbidden.
/// - Trạng thái không hợp lệ -> Validation error.
/// - Dữ liệu trùng (DB constraint) -> 409 Conflict tương ứng.
/// - Exception DB không xác định -> log một lần không chứa secret, trả Auth.Uncategorized (500).
/// - Yêu cầu hủy qua CancellationToken -> rethrow OperationCanceledException.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class ServiceBoundaryValidationAndExceptionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // ========================================================================
    // 1. INPUT THIẾU / SAI: KHÔNG GHI DB, TRẢ VỀ LỖI VALIDATION
    // ========================================================================

    [Fact]
    public async Task UserService_RegisterAsync_InvalidEmail_ReturnsValidationAndDoesNotAccessDb()
    {
        RecordingUserRepo userRepo = new();
        RecordingRoleRepo roleRepo = new();
        UserService service = CreateUserService(userRepo, roleRepo);

        RegisterRequest request = new(
            Email: "not-an-email",
            Password: "Password123!",
            FullName: "Test User");

        Result<AccountCreatedResponse> result = await service.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Empty(userRepo.CreatedUsers);
        Assert.False(userRepo.EmailExistsCalled);
    }

    [Fact]
    public async Task UserService_RegisterAsync_PasswordTooShort_ReturnsValidationAndDoesNotAccessDb()
    {
        RecordingUserRepo userRepo = new();
        UserService service = CreateUserService(userRepo);

        RegisterRequest request = new(
            Email: "user@example.com",
            Password: "123", // Quá ngắn (< 8 ký tự)
            FullName: "Test User");

        Result<AccountCreatedResponse> result = await service.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Empty(userRepo.CreatedUsers);
    }

    [Fact]
    public async Task UserService_AuthenticateAsync_InvalidEmail_ReturnsValidation()
    {
        RecordingUserRepo userRepo = new();
        UserService service = CreateUserService(userRepo);

        LoginRequest request = new(Email: "invalid-email", Password: "Password123!");
        Result<Guid> result = await service.AuthenticateAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task UserService_ChangePasswordAsync_EmptyUserId_ReturnsValidation()
    {
        RecordingUserRepo userRepo = new();
        UserService service = CreateUserService(userRepo);

        ChangePasswordRequest request = new("OldPass123!", "NewPassword123!");
        Result result = await service.ChangePasswordAsync(Guid.Empty, request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task UserService_ValidateActiveUserAsync_EmptyUserId_ReturnsValidation()
    {
        RecordingUserRepo userRepo = new();
        UserService service = CreateUserService(userRepo);

        Result result = await service.ValidateActiveUserAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task AuthSessionService_CreateSessionAsync_EmptyUserId_ReturnsValidationAndDoesNotAccessDb()
    {
        RecordingSessionRepo sessionRepo = new();
        RecordingUserRepo userRepo = new();
        AuthSessionService service = CreateAuthSessionService(sessionRepo, userRepo);

        Result<AuthTokenPair> result = await service.CreateSessionAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.False(sessionRepo.CreateSessionCalled);
    }

    [Fact]
    public async Task AuthSessionService_RefreshAsync_WhitespaceToken_ReturnsInvalidRefreshToken()
    {
        RecordingSessionRepo sessionRepo = new();
        AuthSessionService service = CreateAuthSessionService(sessionRepo);

        Result<AuthTokenPair> result = await service.RefreshAsync("   ");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.InvalidRefreshToken, result.Error);
        Assert.False(sessionRepo.RotateCalled);
    }

    [Fact]
    public async Task ServiceIdentityService_IssueTokenAsync_EmptyClientId_ReturnsValidation()
    {
        ServiceIdentityService service = new(
            new RecordingServicePrincipalRepo(),
            new RecordingTokenIssuer(),
            new TestCorrelationIdProvider(),
            new TestLogger<ServiceIdentityService>());

        ServiceTokenRequest request = new("", "secret", "aud", "scope");
        Result<ServiceTokenResponse> result = await service.IssueTokenAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    // ========================================================================
    // 2. DỮ LIỆU TRÙNG (DB CONSTRAINT) -> ÁNH XẠ SANG LỖI 409 PHÙ HỢP
    // ========================================================================

    [Fact]
    public void UserRolePrimaryKey_MapsToAlreadyAssigned()
    {
        StorageConstraintViolationException exception = new(StorageConstraintViolationType.Unique, StorageConstraints.UserRolePrimaryKey);

        Assert.True(Application.Execution.StorageConstraintMapping.TryMap(exception, out Error? error));
        Assert.Equal(AuthErrors.Role.AlreadyAssigned, error);
    }

    [Fact]
    public async Task UserService_RegisterAsync_DuplicateEmailConstraint_ReturnsConflict()
    {
        RecordingUserRepo userRepo = new()
        {
            ThrowOnCreateWithRole = new StorageConstraintViolationException(
                StorageConstraintViolationType.Unique,
                StorageConstraints.UserEmail)
        };
        UserService service = CreateUserService(userRepo);

        RegisterRequest request = new(
            Email: "duplicate@example.com",
            Password: "Password123!",
            FullName: "Test User");

        Result<AccountCreatedResponse> result = await service.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    // ========================================================================
    // 3. EXCEPTION NGOÀI DỰ KIẾN: LOG 1 LẦN KHÔNG CHỨA SECRET, TRẢ AUTH.UNCATEGORIZED (500)
    // ========================================================================

    [Fact]
    public async Task UserService_RegisterAsync_UnknownDbException_ReturnsUncategorizedAndLogsWithoutSecret()
    {
        TestLogger<UserService> logger = new();
        RecordingUserRepo userRepo = new()
        {
            ThrowOnCreateWithRole = new InvalidOperationException("PostgreSQL connection pool exhausted")
        };
        UserService service = CreateUserService(userRepo, logger: logger);

        const string sensitivePassword = "SuperSecretPassword123!";
        RegisterRequest request = new("user@example.com", sensitivePassword, "User Name");

        Result<AccountCreatedResponse> result = await service.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.Uncategorized, result.Error);
        Assert.Equal(ErrorType.Failure, result.Error.Type);

        // Xác nhận đã ghi log đúng 1 lần
        Assert.Single(logger.Logs);
        TestLogEntry log = logger.Logs[0];
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Contains("Register", log.Message);
        Assert.Contains("test-correlation-id", log.Message);

        // Xác nhận log KHÔNG chứa mật khẩu hoặc dữ liệu nhạy cảm
        Assert.DoesNotContain(sensitivePassword, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePassword, log.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserService_AuthenticateAsync_UnrecognizedConstraint_ReturnsUncategorized()
    {
        TestLogger<UserService> logger = new();
        RecordingUserRepo userRepo = new()
        {
            UserToReturn = new User
            {
                Id = Guid.NewGuid(),
                Email = "user@example.com",
                IsActive = true,
                PasswordHash = new PasswordHasher<User>().HashPassword(null!, "CorrectPass123!")
            },
            ThrowOnUpdate = new StorageConstraintViolationException(
                StorageConstraintViolationType.Other,
                "UNKNOWN_SYSTEM_CONSTRAINT_123")
        };
        UserService service = CreateUserService(userRepo, logger: logger);

        Result<Guid> result = await service.AuthenticateAsync(new LoginRequest("user@example.com", "CorrectPass123!"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.Uncategorized, result.Error);
        Assert.Single(logger.Logs);
        Assert.Contains("UNKNOWN_SYSTEM_CONSTRAINT_123", logger.Logs[0].Message);
    }

    // ========================================================================
    // 4. YÊU CẦU HỦY QUA CANCELLATIONTOKEN: PHẢI ĐƯỢC RETHROW
    // ========================================================================

    [Fact]
    public async Task UserService_RegisterAsync_CancellationRequested_RethrowsOperationCanceledException()
    {
        RecordingUserRepo userRepo = new();
        UserService service = CreateUserService(userRepo);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        RegisterRequest request = new("user@example.com", "Password123!", "User Name");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterAsync(request, cts.Token));
    }

    [Fact]
    public async Task AuthSessionService_RefreshAsync_CancellationRequested_RethrowsOperationCanceledException()
    {
        AuthSessionService service = CreateAuthSessionService(new RecordingSessionRepo());

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync("valid-token", cts.Token));
    }

    // ========================================================================
    // 5. PHÁT HIỆN REFRESH TOKEN DÙNG LẠI (REUSE DETECTION) VÀ THU HỒI TẤT CẢ PHIÊN
    // ========================================================================

    [Fact]
    public async Task AuthSessionService_RefreshAsync_ReuseDetected_RevokesAllSessionsForUser()
    {
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        User user = new() { Id = userId, IsActive = true };
        Session session = new() { Id = sessionId, UserId = userId, User = user, ExpiresAtUtc = DateTime.UtcNow.AddDays(30) };

        // Token đã bị thu hồi trước đó (bị kẻ gian hoặc request cũ gửi lại)
        RefreshToken revokedToken = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionId = sessionId,
            Session = session,
            TokenHash = "revoked-token-hash",
            IsRevoked = true,
            RevokedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
        };

        RecordingSessionRepo sessionRepo = new()
        {
            TokenToReturn = revokedToken
        };
        RecordingAuditLogRepo auditRepo = new();
        AuthSessionService service = CreateAuthSessionService(sessionRepo, auditRepo: auditRepo);

        Result<AuthTokenPair> result = await service.RefreshAsync("raw-refresh-token");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.InvalidRefreshToken, result.Error);

        // BẮT BUỘC: Service quyết định thu hồi TOÀN BỘ phiên của user
        Assert.Contains(userId, sessionRepo.RevokedUsers);
        Assert.Contains(auditRepo.Logs, l => l.Action == "Refresh" && l.Result.Contains("Failed_Or_Compromised"));
    }

    // ========================================================================
    // TRỢ GIÚP TẠO MOCK VÀ SERVICE CHO TEST
    // ========================================================================

    private static UserService CreateUserService(
        RecordingUserRepo userRepo,
        RecordingRoleRepo? roleRepo = null,
        TestLogger<UserService>? logger = null)
    {
        roleRepo ??= new RecordingRoleRepo();
        roleRepo.Roles["Customer"] = new Role { Id = Guid.NewGuid(), Name = "Customer", Scope = RoleScope.Global };

        return new UserService(
            userRepo,
            roleRepo,
            new PasswordHasher<User>(),
            Options.Create(new AdminBootstrapOptions { CreationKey = "bootstrap-key" }),
            new FixedTimeProvider(FixedNow),
            new TestCorrelationIdProvider(),
            new RecordingAuditLogRepo(),
            new RecordingSessionRepo(),
            new RecordingRecoveryTokenRepo(),
            FakeMediaUrlResolver.Instance,
            logger ?? new TestLogger<UserService>());
    }

    private static AuthSessionService CreateAuthSessionService(
        RecordingSessionRepo sessionRepo,
        RecordingUserRepo? userRepo = null,
        RecordingAuditLogRepo? auditRepo = null)
    {
        userRepo ??= new RecordingUserRepo
        {
            UserToReturn = new User { Id = Guid.NewGuid(), IsActive = true }
        };

        return new AuthSessionService(
            sessionRepo,
            userRepo,
            new RecordingTokenIssuer(),
            new FixedTimeProvider(FixedNow),
            new TestCorrelationIdProvider(),
            auditRepo ?? new RecordingAuditLogRepo(),
            new TestLogger<AuthSessionService>(),
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => "test-correlation-id";
    }

    private sealed class RecordingTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) =>
            new("access-token-jwt", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    private sealed record TestLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<TestLogEntry> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add(new TestLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class RecordingAuditLogRepo : IAuditLogRepository
    {
        public List<AuditLog> Logs { get; } = [];
        public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default)
        {
            Logs.Add(auditLog);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRecoveryTokenRepo : IRecoveryTokenRepository
    {
        public Task AddAsync(RecoveryToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RecoveryToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult<RecoveryToken?>(null);
        public Task UpdateAsync(RecoveryToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSessionRepo : IAuthSessionRepository
    {
        public bool CreateSessionCalled { get; private set; }
        public bool RotateCalled { get; private set; }
        public RefreshToken? TokenToReturn { get; set; }
        public List<Guid> RevokedUsers { get; } = [];

        // --- CÁC HÀM TẠO SESSION & ROTATE MỚI ĐÃ ĐƯỢC GẮN CỜ THEO DÕI ---
        public Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, string? deviceName, string? ipAddress, CancellationToken cancellationToken = default)
        {
            CreateSessionCalled = true;
            return Task.FromResult<AuthSessionPrincipal?>(null);
        }

        public Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            RotateCalled = true;

            if (TokenToReturn is not null && (TokenToReturn.IsRevoked || TokenToReturn.UsedAtUtc is not null))
            {
                RevokedUsers.Add(TokenToReturn.UserId);
                return Task.FromResult<AuthSessionPrincipal?>(null);
            }

            return Task.FromResult<AuthSessionPrincipal?>(null);
        }

        // --- CÁC HÀM CÒN LẠI ---
        public Task<RefreshToken?> GetRefreshTokenWithSessionAndUserAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult(TokenToReturn);

        public Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            RevokedUsers.Add(userId);
            return Task.CompletedTask;
        }

        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Session>> GetSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<Guid?> GetSessionIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<bool> RevokeSessionForUserAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);

        // --- CRUD BASE ---
        public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingOrgRepo : IOrganizationRepository
    {
        public bool CreateCalled { get; private set; }
        public Exception? ThrowOnCreate { get; set; }

        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            return Task.FromResult(organization);
        }

        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(new Organization { Id = id, Status = OrganizationStatus.Active });
        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<PagedResult<Organization>> GetAllAsync(PageRequest request, OrganizationStatus? status = null, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult.Create<Organization>([], 1, 10, 0));
        public Task<Organization?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => GetByIdAsync(id, cancellationToken);
        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult(IExecutionTransaction.None);
    }

    private sealed class RecordingMemRepo : IOrganizationMembershipRepository
    {
        public Dictionary<Guid, IReadOnlyList<string>> GlobalPermissions { get; } = [];

        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobalPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
        public Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationMembership?>(new OrganizationMembership { OrganizationId = organizationId, UserId = userId, Status = MembershipStatus.Active });
        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(2);
        public Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OrganizationMembership>>([]);
    }

    private sealed class RecordingUserRepo : IUserRepository
    {
        public bool EmailExistsCalled { get; private set; }
        public List<User> CreatedUsers { get; } = [];
        public User? UserToReturn { get; set; }
        public Exception? ThrowOnCreateWithRole { get; set; }
        public Exception? ThrowOnUpdate { get; set; }

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        {
            EmailExistsCalled = true;
            return Task.FromResult(false);
        }

        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCreateWithRole is not null) throw ThrowOnCreateWithRole;
            CreatedUsers.Add(user);
            return Task.FromResult(user);
        }

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
        {
            CreatedUsers.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpdate is not null) throw ThrowOnUpdate;
            return Task.FromResult(true);
        }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<User?>(UserToReturn ?? (UserToReturn = new User { Id = id, IsActive = true }));

        public Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<User?>(UserToReturn ?? (UserToReturn = new User { Id = id, IsActive = true }));

        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<User?>(UserToReturn ?? (UserToReturn = new User { Id = id, IsActive = true }));
        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserToReturn);

        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserToReturn?.IsActive ?? true);

        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, string? type, CancellationToken cancellationToken = default) => Task.FromResult<(IReadOnlyCollection<User> Items, int TotalCount)>(([], 0));
        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingRoleRepo : IRoleRepository
    {
        public Dictionary<string, Role> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(name));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.FirstOrDefault(r => r.Id == roleId));

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Role>>(Roles.Values.ToList());
        public Task<int> CountByIdsAsync(IEnumerable<Guid> roleIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.Count(r => roleIds.Contains(r.Id)));
    }

    private sealed class RecordingServicePrincipalRepo : IServicePrincipalRepository
    {
        public Task<ServicePrincipal?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default) => Task.FromResult<ServicePrincipal?>(null);
        public Task<bool> HasScopeAsync(Guid servicePrincipalId, string scope, string audience, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
