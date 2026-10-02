using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.DTOs.User;


using Xunit;
using Dprental.AuthService.Domain.ValueObjects;

namespace Dprental.AuthService.Api.Tests;

/// <summary>Kiểm tra use case tài khoản người dùng mà không phụ thuộc DB thật.</summary>
public sealed class UserAccountServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteProfileAsyncHappyPathSucceedsAndAudits()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", FullName = "Original Name", IsActive = true };

        FakeUserRepository userRepo = new() { User = user };
        TestAuditLogRepository auditRepo = new();
        UserAccountService service = CreateService(userRepo, auditLogRepository: auditRepo);

        Guid mediaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        CompleteProfileRequest request = new(
            CitizenId: "079203001234",
            PhoneNumber: "0901234567",
            DateOfBirth: new DateOnly(2000, 1, 1),
            FullName: "Updated Name",
            AvatarMediaId: mediaId);

        Result<CurrentUserResponse> result = await service.CompleteProfileAsync(userId, request);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsProfileCompleted);
        Assert.Equal("********1234", result.Value.MaskedCitizenId);
        Assert.Equal("0901234567", result.Value.PhoneNumber);
        Assert.Equal("Updated Name", result.Value.FullName);
        Assert.Equal(mediaId, result.Value.AvatarMediaId);
        Assert.Equal("http://localhost:5050/api/v1/media/11111111-1111-1111-1111-111111111111/content", result.Value.AvatarUrl);
        Assert.Equal(new DateOnly(2000, 1, 1), result.Value.DateOfBirth);
        Assert.NotNull(user.ProfileCompletedAtUtc);

        AuditLog log = Assert.Single(auditRepo.Logs);
        Assert.Equal("Profile_Complete", log.Action);
        Assert.Equal("Success", log.Result);
        Assert.Equal(userId.ToString(), log.Target);
        Assert.DoesNotContain("079203001234", log.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteProfileAsyncAlreadyCompletedReturnsConflict()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", FullName = "Name", CitizenId = "079203001234", ProfileCompletedAtUtc = DateTime.UtcNow, IsActive = true };
        FakeUserRepository userRepo = new() { User = user };
        UserAccountService service = CreateService(userRepo);
        CompleteProfileRequest request = new(CitizenId: "079203001234", PhoneNumber: "0901234567", DateOfBirth: new DateOnly(2000, 1, 1));

        Result<CurrentUserResponse> result = await service.CompleteProfileAsync(userId, request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Profile.AlreadyCompleted, result.Error);
    }

    [Fact]
    public async Task CompleteProfileAsyncUnderageReturnsBadRequest()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user };
        UserAccountService service = CreateService(userRepo);

        CompleteProfileRequest request = new(CitizenId: "079203001234", PhoneNumber: "0901234567", DateOfBirth: new DateOnly(2010, 1, 1));
        Result<CurrentUserResponse> result = await service.CompleteProfileAsync(userId, request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.Underage, result.Error);
    }

    [Fact]
    public async Task CompleteProfileAsyncCitizenIdExistsReturnsConflict()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user, ExistingCitizenId = "079203001234" };
        UserAccountService service = CreateService(userRepo);

        CompleteProfileRequest request = new(CitizenId: "079203001234", PhoneNumber: "0901234567", DateOfBirth: new DateOnly(2000, 1, 1));
        Result<CurrentUserResponse> result = await service.CompleteProfileAsync(userId, request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.CitizenIdAlreadyExists, result.Error);
    }

    [Fact]
    public async Task CompleteProfileAsyncDuplicateCitizenIdOnSaveReturnsConflict()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user, ThrowDuplicateCitizenIdOnUpdate = true };
        UserAccountService service = CreateService(userRepo);

        CompleteProfileRequest request = new(CitizenId: "079203001234", PhoneNumber: "0901234567", DateOfBirth: new DateOnly(2000, 1, 1));
        await Assert.ThrowsAsync<StorageConstraintViolationException>(() => service.CompleteProfileAsync(userId, request));
    }

    [Fact]
    public async Task UpdateProfileAsyncEmptyPatchReturnsBadRequest()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        UserAccountService service = CreateService(new FakeUserRepository { User = user });

        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, new UpdateProfileRequest());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Profile.EmptyPatch, result.Error);
    }

    [Fact]
    public async Task UpdateProfileAsyncBlankFullNameReturnsInvalidFullName()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        UserAccountService service = CreateService(new FakeUserRepository { User = user });

        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, new UpdateProfileRequest(FullName: "   "));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Profile.InvalidFullName, result.Error);
    }

    [Fact]
    public async Task UpdateProfileAsyncWithRemoveAvatarClearsAvatar()
    {
        Guid userId = Guid.NewGuid();
        Guid oldMediaId = Guid.NewGuid();
        User user = new()
        {
            Id = userId,
            Email = "user@example.com",
            AvatarMediaId = oldMediaId,
            IsActive = true
        };
        FakeUserRepository userRepo = new() { User = user };
        UserAccountService service = CreateService(userRepo);

        UpdateProfileRequest request = new(RemoveAvatar: true);

        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, request);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.AvatarMediaId);
        Assert.Null(result.Value.AvatarUrl);
        Assert.Null(user.AvatarMediaId);
    }

    [Fact]
    public async Task UpdateProfileAsyncWithCitizenIdUpdatesCitizenIdAndCompletesProfile()
    {
        Guid userId = Guid.NewGuid();
        User user = new()
        {
            Id = userId,
            Email = "user@example.com",
            PhoneNumber = "0901234567",
            DateOfBirth = new DateOnly(1995, 1, 1),
            ProfileCompletedAtUtc = null,
            IsActive = true
        };
        FakeUserRepository userRepo = new() { User = user };
        UserAccountService service = CreateService(userRepo);

        UpdateProfileRequest request = new(CitizenId: "079203001234");
        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("079203001234", user.CitizenId);
        Assert.NotNull(user.ProfileCompletedAtUtc);
    }

    [Fact]
    public async Task UpdateProfileAsyncDuplicateCitizenIdReturnsCitizenIdAlreadyExists()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user, ExistingCitizenId = "079203001234" };
        UserAccountService service = CreateService(userRepo);

        UpdateProfileRequest request = new(CitizenId: "079203001234");
        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, request);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.CitizenIdAlreadyExists, result.Error);
    }

    [Fact]
    public async Task UpdateUserProfileAsyncWithAllFieldsUpdatesUser()
    {
        Guid userId = Guid.NewGuid();
        User user = new()
        {
            Id = userId,
            Email = "user@example.com",
            FullName = "Old Name",
            IsActive = true
        };
        FakeUserRepository userRepo = new() { User = user };
        UserAccountService service = CreateService(userRepo);

        UpdateUserProfileRequest request = new(
            FullName: "New Full Name",
            PhoneNumber: "0909999999",
            DateOfBirth: new DateOnly(1990, 5, 20),
            CitizenId: "079203009999");

        Result<CurrentUserResponse> result = await service.UpdateUserProfileAsync(userId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("New Full Name", user.FullName);
        Assert.Equal("0909999999", user.PhoneNumber);
        Assert.Equal(new DateOnly(1990, 5, 20), user.DateOfBirth);
        Assert.Equal("079203009999", user.CitizenId);
        Assert.NotNull(user.ProfileCompletedAtUtc);
    }

    [Fact]
    public async Task GetExternalLoginsAsyncReturnsLoginsForUser()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeExternalLoginRepository loginRepo = new();
        loginRepo.Logins.Add(new ExternalLogin { Id = Guid.NewGuid(), UserId = userId, Provider = "Google", Email = "user@example.com", CreatedAtUtc = FixedNow.UtcDateTime });

        UserAccountService service = CreateService(new FakeUserRepository { User = user }, externalLoginRepository: loginRepo);
        Result<IReadOnlyList<ExternalLoginResponse>> result = await service.GetExternalLoginsAsync(userId);

        Assert.True(result.IsSuccess);
        ExternalLoginResponse item = Assert.Single(result.Value);
        Assert.Equal("Google", item.Provider);
        Assert.Equal("user@example.com", item.Email);
    }

    [Fact]
    public async Task LinkExternalLoginAsyncWithGoogleTokenAddsLoginAndLogsAudit()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true, HasPassword = true };
        FakeUserRepository userRepo = new() { User = user };
        FakeExternalLoginRepository loginRepo = new();
        TestAuditLogRepository auditRepo = new();
        FakeFirebaseIdentityService firebase = new();
        UserAccountService service = CreateService(
            userRepo,
            auditLogRepository: auditRepo,
            externalLoginRepository: loginRepo,
            firebaseIdentityService: firebase);

        Result result = await service.LinkExternalLoginAsync(userId, "Google", "firebase-token");

        Assert.True(result.IsSuccess);
        ExternalLogin login = Assert.Single(loginRepo.Logins);
        Assert.Equal(userId, login.UserId);
        Assert.Equal("Google", login.Provider);
        Assert.Equal("google-subject", login.ProviderSubject);
        Assert.Equal("google@example.com", login.Email);
        Assert.Equal("firebase-token", firebase.LastIdToken);
        AuditLog audit = Assert.Single(auditRepo.Logs);
        Assert.Equal("ExternalLogin_Link", audit.Action);
        Assert.Equal(userId.ToString(), audit.Target);
    }

    [Fact]
    public async Task LinkExternalLoginAsyncWhenUserAlreadyHasProviderReturnsAlreadyLinked()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user };
        FakeExternalLoginRepository loginRepo = new();
        loginRepo.Logins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = "Google",
            ProviderSubject = "old-subject",
            Email = "old@example.com"
        });
        UserAccountService service = CreateService(userRepo, externalLoginRepository: loginRepo);

        Result result = await service.LinkExternalLoginAsync(userId, "Google", "firebase-token");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.AlreadyLinked, result.Error);
    }

    [Fact]
    public async Task LinkExternalLoginAsyncWhenGoogleAccountBelongsToAnotherUserReturnsAlreadyLinked()
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user };
        FakeExternalLoginRepository loginRepo = new();
        loginRepo.Logins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            Provider = "Google",
            ProviderSubject = "google-subject",
            Email = "google@example.com"
        });
        UserAccountService service = CreateService(userRepo, externalLoginRepository: loginRepo);

        Result result = await service.LinkExternalLoginAsync(userId, "Google", "firebase-token");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.AlreadyLinked, result.Error);
    }

    [Fact]
    public async Task LinkExternalLoginAsyncWithNonGoogleFirebaseTokenReturnsUnsupportedProvider()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        FakeUserRepository userRepo = new() { User = user };
        FakeFirebaseIdentityService firebase = new()
        {
            VerificationResult = Result.Success(new FirebaseIdentity(
                "firebase-uid",
                "password-subject",
                "password",
                "user@example.com",
                true,
                "User",
                null))
        };
        UserAccountService service = CreateService(userRepo, firebaseIdentityService: firebase);

        Result result = await service.LinkExternalLoginAsync(userId, "Google", "firebase-token");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.FirebaseProviderUnsupported, result.Error);
    }

    [Fact]
    public async Task UnlinkExternalLoginAsyncUnknownProviderReturnsNotLinked()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        UserAccountService service = CreateService(new FakeUserRepository { User = user });

        Result result = await service.UnlinkExternalLoginAsync(userId, "Facebook");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.NotLinked, result.Error);
    }

    [Fact]
    public async Task UnlinkExternalLoginAsyncWhenUserHasNoPasswordAndSingleLoginReturnsCannotUnlinkLastCredential()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "google@example.com", IsActive = true, HasPassword = false };
        FakeExternalLoginRepository loginRepo = new();
        loginRepo.Logins.Add(new ExternalLogin { Id = Guid.NewGuid(), UserId = userId, Provider = "Google", ProviderSubject = "sub-123", Email = "google@example.com" });

        UserAccountService service = CreateService(new FakeUserRepository { User = user }, externalLoginRepository: loginRepo);
        Result result = await service.UnlinkExternalLoginAsync(userId, "Google");

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.CannotUnlinkLastCredential, result.Error);
    }

    [Fact]
    public async Task UnlinkExternalLoginAsyncWhenUserHasPasswordSucceedsAndLogsAudit()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true, HasPassword = true };
        FakeExternalLoginRepository loginRepo = new();
        loginRepo.Logins.Add(new ExternalLogin { Id = Guid.NewGuid(), UserId = userId, Provider = "Google", ProviderSubject = "sub-123", Email = "user@example.com" });
        TestAuditLogRepository auditRepo = new();

        UserAccountService service = CreateService(new FakeUserRepository { User = user }, auditLogRepository: auditRepo, externalLoginRepository: loginRepo);
        Result result = await service.UnlinkExternalLoginAsync(userId, "Google");

        Assert.True(result.IsSuccess);
        Assert.Empty(loginRepo.Logins);
        AuditLog audit = Assert.Single(auditRepo.Logs);
        Assert.Equal("ExternalLogin_Unlink", audit.Action);
        Assert.Equal(userId.ToString(), audit.Target);
    }

    [Fact]
    public async Task DeactivateAsyncWhenLastOwnerReturnsLastOwnerOrganization()
    {
        Guid userId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "owner@example.com", IsActive = true };
        FakeOrganizationMembershipRepository memRepo = new() { ActiveOwnersCount = 1 };
        memRepo.Memberships.Add(new OrganizationMembership { Id = Guid.NewGuid(), UserId = userId, OrganizationId = orgId, Status = MembershipStatus.Active, Role = new Role { Id = Guid.NewGuid(), Name = "Owner" } });

        UserAccountService service = CreateService(new FakeUserRepository { User = user }, organizationMembershipRepository: memRepo);
        Result result = await service.DeactivateAsync(userId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.LastOwnerOrganization, result.Error);
        Assert.True(user.IsActive);
    }

    [Fact]
    public async Task DeactivateAsyncHappyPathDeactivatesUserRevokesSessionsAndLogsAudit()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "member@example.com", IsActive = true };
        FakeAuthSessionRepository sessionRepo = new();
        TestAuditLogRepository auditRepo = new();

        UserAccountService service = CreateService(new FakeUserRepository { User = user }, auditLogRepository: auditRepo, authSessionRepository: sessionRepo);
        Result result = await service.DeactivateAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.False(user.IsActive);
        Assert.Contains(userId, sessionRepo.RevokedUsers);
        AuditLog audit = Assert.Single(auditRepo.Logs);
        Assert.Equal("Account_Deactivate", audit.Action);
        Assert.Equal(userId.ToString(), audit.Target);
    }

    [Fact]
    public async Task GetCitizenIdAsyncReturnsFullCitizenIdAndAuditsWithoutLeakingIt()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", CitizenId = "079203001234", IsActive = true };
        TestAuditLogRepository auditRepo = new();
        UserAccountService service = CreateService(new FakeUserRepository { User = user }, auditLogRepository: auditRepo);

        Result<CitizenIdResponse> result = await service.GetCitizenIdAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("079203001234", result.Value.CitizenId);
        AuditLog audit = Assert.Single(auditRepo.Logs);
        Assert.Equal("Profile_ViewCitizenId", audit.Action);
        Assert.DoesNotContain("079203001234", $"{audit.Target} {audit.Result}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCitizenIdAsyncReturnsNullWithoutAuditWhenNotProvided()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", IsActive = true };
        TestAuditLogRepository auditRepo = new();
        UserAccountService service = CreateService(new FakeUserRepository { User = user }, auditLogRepository: auditRepo);

        Result<CitizenIdResponse> result = await service.GetCitizenIdAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.CitizenId);
        Assert.Empty(auditRepo.Logs);
    }

    [Fact]
    public async Task GetCitizenIdAsyncRejectsInactiveUser()
    {
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "user@example.com", CitizenId = "079203001234", IsActive = false };
        UserAccountService service = CreateService(new FakeUserRepository { User = user });

        Result<CitizenIdResponse> result = await service.GetCitizenIdAsync(userId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Auth.InactiveAccount, result.Error);
    }

    internal static UserAccountService CreateService(
        FakeUserRepository userRepo,
        IAuditLogRepository? auditLogRepository = null,
        FakeExternalLoginRepository? externalLoginRepository = null,
        IFirebaseIdentityService? firebaseIdentityService = null,
        FakeAuthSessionRepository? authSessionRepository = null,
        FakeOrganizationMembershipRepository? organizationMembershipRepository = null,
        IOrganizationRepository? organizationRepository = null,
        FakeMediaServiceClient? mediaServiceClient = null,
        FakeMediaOutboxService? mediaOutboxService = null)
    {
        var mediaClient = mediaServiceClient ?? new FakeMediaServiceClient { AutoVerify = true, DefaultUserId = userRepo.User?.Id };
        return new UserAccountService(
            userRepo,
            externalLoginRepository ?? new FakeExternalLoginRepository(),
            firebaseIdentityService ?? new FakeFirebaseIdentityService(),
            authSessionRepository ?? new FakeAuthSessionRepository(),
            organizationMembershipRepository ?? new FakeOrganizationMembershipRepository(),
            organizationRepository ?? new FakeOrgRepo(),
            auditLogRepository ?? new TestAuditLogRepository(),
            new TestCorrelationIdProvider(),
            new FixedTimeProvider(FixedNow),
            mediaClient,
            FakeMediaUrlResolver.Instance,
            mediaOutboxService ?? new FakeMediaOutboxService());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    internal sealed class TestCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => "test-corr-id";
    }

    internal sealed class TestAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLog> Logs { get; } = [];
        public Task AddAsync(AuditLog log, CancellationToken cancellationToken = default) { Logs.Add(log); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLog>> GetRecentLogsAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<AuditLog>([], 1, 20, 0));
    }

    internal sealed class FakeUserRepository : IUserRepository
    {
        public User? User { get; set; }
        public string? ExistingCitizenId { get; set; }
        public bool ThrowDuplicateCitizenIdOnUpdate { get; set; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(User is not null && User.Id == id ? User : null);
        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(User is not null && User.Id == id ? User : null);
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) => Task.FromResult(citizenId == ExistingCitizenId);

        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default)
        {
            if (ThrowDuplicateCitizenIdOnUpdate) throw new StorageConstraintViolationException(StorageConstraintViolationType.Unique, StorageConstraints.UserCitizenId);
            User = entity;
            return Task.FromResult(true);
        }

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // Đã thêm string? type vào hàm này để khớp với Interface mới
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, string? type, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(User is not null && User.Id == userId ? User.IsActive : true);
        public Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(User is not null && User.Id == id ? User : null);
    }

    internal sealed class FakeExternalLoginRepository : IExternalLoginRepository
    {
        public List<ExternalLogin> Logins { get; } = [];

        public Task<ExternalLogin?> FindAsync(string provider, string providerSubject, CancellationToken cancellationToken = default) =>
            Task.FromResult(Logins.FirstOrDefault(l =>
                string.Equals(l.Provider, provider, StringComparison.Ordinal) &&
                string.Equals(l.ProviderSubject, providerSubject, StringComparison.Ordinal)));

        public Task AddAsync(ExternalLogin login, CancellationToken cancellationToken = default)
        {
            if (Logins.Any(l =>
                    string.Equals(l.Provider, login.Provider, StringComparison.Ordinal) &&
                    string.Equals(l.ProviderSubject, login.ProviderSubject, StringComparison.Ordinal)) ||
                Logins.Any(l =>
                    l.UserId == login.UserId &&
                    string.Equals(l.Provider, login.Provider, StringComparison.OrdinalIgnoreCase)))
            {
                throw new StorageConstraintViolationException(
                    StorageConstraintViolationType.Unique,
                    StorageConstraints.ExternalLoginProviderSubject);
            }

            Logins.Add(login);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExternalLogin>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalLogin>>(Logins.Where(l => l.UserId == userId).ToList());

        public Task<bool> DeleteAsync(Guid userId, string provider, CancellationToken cancellationToken = default)
        {
            int removed = Logins.RemoveAll(l => l.UserId == userId && l.Provider == provider);
            return Task.FromResult(removed > 0);
        }
    }

    private sealed class FakeFirebaseIdentityService : IFirebaseIdentityService
    {
        public string? LastIdToken { get; private set; }

        public Result<FirebaseIdentity> VerificationResult { get; set; } = Result.Success(new FirebaseIdentity(
            "firebase-uid",
            "google-subject",
            "google.com",
            "google@example.com",
            true,
            "Google User",
            null));

        public Task<Result<FirebaseIdentity>> VerifyIdTokenAsync(string? idToken, CancellationToken cancellationToken = default)
        {
            LastIdToken = idToken;
            return Task.FromResult(VerificationResult);
        }
    }

    internal sealed class FakeAuthSessionRepository : IAuthSessionRepository
    {
        public List<Guid> RevokedUsers { get; } = [];

        public Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, string? deviceName, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RefreshToken?> GetRefreshTokenWithSessionAndUserAsync(string tokenHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // Đã thay thế RotateTokenAsync bằng RotateAsync chuẩn theo Interface mới
        public Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            RevokedUsers.Add(userId);
            return Task.CompletedTask;
        }

        public Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Session>> GetSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSessionIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeSessionForUserAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class FakeOrganizationMembershipRepository : IOrganizationMembershipRepository
    {
        public List<OrganizationMembership> Memberships { get; } = [];
        public int ActiveOwnersCount { get; set; } = 1;

        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveOwnersCount);

        public Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(
            Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrganizationMembership>>(
            Memberships.Where(m => m.UserId == userId).ToList());

        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default)
        {
            _ = this;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
    internal sealed class FakeOrgRepo : IOrganizationRepository
    {
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<PagedResult<Organization>> GetAllAsync(
            PageRequest request,
            OrganizationStatus? status = null,
            OrganizationType? type = null,
            string? province = null,
            bool overdueOnly = false,
            bool remindedOnly = false,
            DateTime? overdueCutoffUtc = null,
            CancellationToken cancellationToken = default) => Task.FromResult(PagedResult.Create<Organization>([], 1, 10, 0));
        public Task<Organization?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default) => Task.FromResult(organization);
        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult(IExecutionTransaction.None);
    }
}