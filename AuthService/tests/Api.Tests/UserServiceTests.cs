using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class UserServiceTests
{
    [Fact]
    public async Task RegisterAsyncCreatesActiveUserWithNormalizedEmailAndHashedPassword()
    {
        DateTimeOffset now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
        RecordingUserRepository repository = new();
        PasswordHasher<User> hasher = new();
        UserService service = CreateService(repository, hasher, new FixedTimeProvider(now));

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("  Tan.Len@Example.COM ", "Matkhau123!", "  Huynh Tan Len "));

        Assert.True(result.IsSuccess);
        User created = Assert.Single(repository.CreatedUsers);
        Assert.Equal(result.Value.AccountId, created.Id);
        Assert.Equal("tan.len@example.com", created.Email);
        Assert.Equal("Huynh Tan Len", created.FullName);
        Assert.True(created.IsActive);
        Assert.Equal(now.UtcDateTime, created.CreatedAt);
        Assert.NotEqual("Matkhau123!", created.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(created, created.PasswordHash, "Matkhau123!"));
    }

    [Fact]
    public async Task RegisterAsyncExistingEmailReturnsConflictWithoutSaving()
    {
        RecordingUserRepository repository = new() { ExistingEmail = "tan.len@example.com" };
        UserService service = CreateService(repository, new PasswordHasher<User>(), TimeProvider.System);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("TAN.LEN@example.com", "Matkhau123!", "Huynh Tan Len"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Empty(repository.CreatedUsers);
    }

    [Fact]
    public async Task RegisterAsyncConcurrentDuplicateRejectedByDatabaseReturnsConflict()
    {
        RecordingUserRepository repository = new() { RejectCreate = true };
        UserService service = CreateService(repository, new PasswordHasher<User>(), TimeProvider.System);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("tan.len@example.com", "Matkhau123!", "Huynh Tan Len"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
    }

    [Fact]
    public async Task GetCurrentUserProfileAsyncCombinesGlobalAndActiveOrganizationAccess()
    {
        Guid userId = Guid.NewGuid();
        DateTime createdAt = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        User user = new()
        {
            Id = userId,
            Email = "tan.len@example.com",
            FullName = "Huynh Tan Len",
            CreatedAt = createdAt,
            IsActive = true,
        };
        user.UserRoles.Add(new UserRole { UserId = userId, Role = CreateRole("Admin", "roles.assign") });

        Organization activeOrg = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", Type = OrganizationType.Supplier, IsActive = true };
        Organization suspendedOrg = new() { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta", Type = OrganizationType.Renter, IsActive = true };
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            UserId = userId,
            OrganizationId = activeOrg.Id,
            Organization = activeOrg,
            Role = CreateRole("Owner", "roles.revoke"),
            Status = MembershipStatus.Active,
        });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            UserId = userId,
            OrganizationId = suspendedOrg.Id,
            Organization = suspendedOrg,
            Role = CreateRole("Viewer", "orders.view"),
            Status = MembershipStatus.Suspended,
        });

        // Đã sửa lại để gọi CreateService thay vì new UserService(...)
        UserService service = CreateService(new RecordingUserRepository { ProfileUser = user }, new PasswordHasher<User>(), TimeProvider.System);

        Result<CurrentUserResponse> result = await service.GetCurrentUserProfileAsync(userId);

        Assert.True(result.IsSuccess);
        CurrentUserResponse profile = result.Value;
        Assert.Equal(userId, profile.Id);
        Assert.Equal("tan.len@example.com", profile.Email);
        Assert.Equal("Huynh Tan Len", profile.FullName);
        Assert.Equal(createdAt, profile.CreatedAt);
        Assert.Equal(["Admin"], profile.Roles);
        Assert.Equal(["roles.assign", "roles.revoke"], profile.Permissions.Order(StringComparer.Ordinal));
        UserOrganizationResponse organization = Assert.Single(profile.Organizations);
        Assert.Equal(activeOrg.Id, organization.OrganizationId);
        Assert.Equal("Acme", organization.Name);
        Assert.Equal("acme", organization.Slug);
        Assert.Equal(OrganizationType.Supplier, organization.Type);
        Assert.Equal("Owner", organization.RoleName);
    }

    [Fact]
    public async Task GetCurrentUserProfileAsyncMissingOrInactiveUserReturnsNotFound()
    {
        // Đã sửa lại để gọi CreateService thay vì new UserService(...)
        UserService service = CreateService(new RecordingUserRepository(), new PasswordHasher<User>(), TimeProvider.System);

        Result<CurrentUserResponse> result = await service.GetCurrentUserProfileAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.UserNotFound, result.Error);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    // ========================================================================
    // HÀM TIỆN ÍCH TẠO SERVICE ĐỂ DÙNG CHUNG TRONG CÁC TEST CASE
    // ========================================================================
    private static UserService CreateService(
        IUserRepository userRepository,
        IPasswordHasher<User> passwordHasher,
        TimeProvider timeProvider)
    {
        // Vì RegisterAsync và GetCurrentUserProfileAsync KHÔNG gọi đến 3 dependency này,
        // nên ta có thể truyền null! để test pass. (Nếu các hàm khác cần, ta sẽ phải dùng Mock/Fake).
        return new UserService(
            userRepository,
            passwordHasher,
            timeProvider,
            correlationIdProvider: null!,
            auditLogRepository: null!,
            authSessionRepository: null!,
            recoveryTokenRepository: null!
        );
    }

    private static Role CreateRole(string name, params string[] permissionCodes)
    {
        Role role = new() { Id = Guid.NewGuid(), Name = name };
        foreach (string code in permissionCodes)
        {
            role.RolePermissions.Add(new RolePermission { Role = role, Permission = new Permission { Id = Guid.NewGuid(), Code = code } });
        }

        return role;
    }

    private sealed class RecordingUserRepository : IUserRepository
    {
        public string? ExistingEmail { get; init; }
        public bool RejectCreate { get; init; }
        public User? ProfileUser { get; init; }
        public List<User> CreatedUsers { get; } = [];

        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfileUser is not null && ProfileUser.Id == id ? ProfileUser : null);

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(normalizedEmail == ExistingEmail);

        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not authenticate users.");

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
        {
            if (RejectCreate)
            {
                throw new DuplicateEmailException();
            }

            CreatedUsers.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not use generic CRUD reads.");

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not use generic CRUD reads.");

        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not update users.");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not delete users.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}