using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Application.Validators;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class UserServiceTests
{
    private const string ValidBootstrapKey = "super-secret-bootstrap-key";

    [Fact]
    public async Task RegisterAsyncCreatesActiveUserWithNormalizedEmailAndHashedPassword()
    {
        DateTimeOffset now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
        RecordingUserRepository repository = new();
        PasswordHasher<User> hasher = new();
        UserService service = CreateService(repository, passwordHasher: hasher, timeProvider: new FixedTimeProvider(now));

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
        Assert.Empty(repository.CreatedWithRole);
    }

    [Fact]
    public async Task RegisterAsyncStoresProfileFieldsAndLeavesCitizenIdEmpty()
    {
        RecordingUserRepository repository = new();
        UserService service = CreateService(repository);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("a@example.com", "Matkhau123!", "A", "https://cdn.example.com/a.png", "0901234567", new DateOnly(2000, 1, 1)));

        Assert.True(result.IsSuccess);
        User created = Assert.Single(repository.CreatedUsers);
        // Đăng ký không thu thập CCCD — chỉ được set sau, qua đăng ký tổ chức hoặc hoàn thiện hồ sơ để thuê cá nhân.
        Assert.Null(created.CitizenId);
        Assert.Equal("https://cdn.example.com/a.png", created.AvatarUrl);
        Assert.Equal("0901234567", created.PhoneNumber);
        Assert.Equal(new DateOnly(2000, 1, 1), created.DateOfBirth);
    }

    [Fact]
    public async Task RegisterAsyncRejectsUnderageUser()
    {
        DateTimeOffset now = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        RecordingUserRepository repository = new();
        UserService service = CreateService(repository, timeProvider: new FixedTimeProvider(now));

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("a@example.com", "Matkhau123!", "A", DateOfBirth: new DateOnly(2008, 9, 23)));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.Underage, result.Error);
        Assert.Empty(repository.CreatedUsers);
    }

    [Fact]
    public async Task GetCurrentUserProfileAsyncMasksCitizenId()
    {
        // CitizenId được set trực tiếp trên entity — mô phỏng trạng thái sau khi user đã hoàn thiện hồ sơ
        // hoặc đăng ký tổ chức (đăng ký customer không thu thập CCCD).
        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Email = "a@example.com", FullName = "A", CitizenId = "079203001234", AvatarUrl = "https://cdn.example.com/a.png", IsActive = true };
        UserService service = CreateService(new RecordingUserRepository { ProfileUser = user });

        Result<CurrentUserResponse> result = await service.GetCurrentUserProfileAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("********1234", result.Value.MaskedCitizenId);
        Assert.Equal("https://cdn.example.com/a.png", result.Value.AvatarUrl);
    }

    [Fact]
    public async Task RegisterAsyncAssignsCustomerRoleWhenSeeded()
    {
        RecordingUserRepository repository = new();
        FakeRoleRepository roleRepo = new();
        Guid customerRoleId = Guid.NewGuid();
        roleRepo.Roles[SystemRoleCode.Customer] = new Role { Id = customerRoleId, Name = SystemRoleCode.Customer, Scope = RoleScope.Global };
        UserService service = CreateService(repository, roleRepository: roleRepo);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("new.user@example.com", "Matkhau123!", "New User"));

        Assert.True(result.IsSuccess);
        Assert.Single(repository.CreatedUsers);
        var (assignedUser, assignedRoleId) = Assert.Single(repository.CreatedWithRole);
        Assert.Equal(result.Value.AccountId, assignedUser.Id);
        Assert.Equal(customerRoleId, assignedRoleId);
    }

    [Fact]
    public async Task RegisterAsyncExistingEmailReturnsConflictWithoutSaving()
    {
        RecordingUserRepository repository = new() { ExistingEmail = "tan.len@example.com" };
        UserService service = CreateService(repository);

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
        UserService service = CreateService(repository);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("tan.len@example.com", "Matkhau123!", "Huynh Tan Len"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wrong-key")]
    public async Task RegisterAdminAsyncWithMissingOrInvalidKeyReturnsUnauthorized401(string? invalidKey)
    {
        RecordingUserRepository repository = new();
        UserService service = CreateService(repository);

        Result<AccountCreatedResponse> result = await service.RegisterAdminAsync(
            new RegisterAdminRequest("admin@example.com", "Password123!", "Admin User"),
            invalidKey);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.AdminBootstrap.InvalidKey, result.Error);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        Assert.Empty(repository.CreatedUsers);
        Assert.Empty(repository.CreatedWithRole);
    }

    [Fact]
    public async Task RegisterAdminAsyncWithValidKeyCreatesAdminUserAndAssignsRole()
    {
        RecordingUserRepository repository = new();
        FakeRoleRepository roleRepo = new();
        Guid adminRoleId = Guid.NewGuid();
        roleRepo.Roles[SystemRoleCode.Admin] = new Role { Id = adminRoleId, Name = SystemRoleCode.Admin, Scope = RoleScope.Global };

        UserService service = CreateService(repository, roleRepository: roleRepo);

        Result<AccountCreatedResponse> result = await service.RegisterAdminAsync(
            new RegisterAdminRequest("  Admin@Example.com ", "AdminPassword123!", "  Super Admin  "),
            ValidBootstrapKey);

        Assert.True(result.IsSuccess);
        User created = Assert.Single(repository.CreatedUsers);
        Assert.Equal("admin@example.com", created.Email);
        Assert.Equal("Super Admin", created.FullName);
        Assert.True(created.IsActive);

        var (assignedUser, assignedRoleId) = Assert.Single(repository.CreatedWithRole);
        Assert.Equal(created.Id, assignedUser.Id);
        Assert.Equal(adminRoleId, assignedRoleId);
    }

    [Fact]
    public async Task RegisterAdminAsyncExistingEmailReturnsConflictWithoutCreating()
    {
        RecordingUserRepository repository = new() { ExistingEmail = "admin@example.com" };
        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[SystemRoleCode.Admin] = new Role { Id = Guid.NewGuid(), Name = SystemRoleCode.Admin, Scope = RoleScope.Global };

        UserService service = CreateService(repository, roleRepository: roleRepo);

        Result<AccountCreatedResponse> result = await service.RegisterAdminAsync(
            new RegisterAdminRequest("admin@example.com", "Password123!", "Admin"),
            ValidBootstrapKey);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Empty(repository.CreatedUsers);
        Assert.Empty(repository.CreatedWithRole);
    }

    [Fact]
    public async Task RegisterAdminAsyncWhenAdminRoleNotFoundReturnsNotFoundAndRollback()
    {
        RecordingUserRepository repository = new();
        FakeRoleRepository roleRepo = new(); // no admin role in database

        UserService service = CreateService(repository, roleRepository: roleRepo);

        Result<AccountCreatedResponse> result = await service.RegisterAdminAsync(
            new RegisterAdminRequest("admin@example.com", "Password123!", "Admin"),
            ValidBootstrapKey);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.RoleNotFound, result.Error);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Empty(repository.CreatedUsers);
        Assert.Empty(repository.CreatedWithRole);
    }

    [Fact]
    public void AdminBootstrapOptionsValidatorRejectsMissingOrEmptyKey()
    {
        AdminBootstrapOptionsValidator validator = new();

        ValidateOptionsResult resultEmpty = validator.Validate(null, new AdminBootstrapOptions { CreationKey = "" });
        Assert.True(resultEmpty.Failed);
        Assert.Contains("Auth:AdminBootstrap:CreationKey must be configured.", resultEmpty.FailureMessage);

        ValidateOptionsResult resultWhitespace = validator.Validate(null, new AdminBootstrapOptions { CreationKey = "   " });
        Assert.True(resultWhitespace.Failed);

        ValidateOptionsResult resultValid = validator.Validate(null, new AdminBootstrapOptions { CreationKey = "secret-key" });
        Assert.True(resultValid.Succeeded);
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

        Organization activeOrg = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", Type = OrganizationType.Supplier, Status = OrganizationStatus.Active };
        Organization suspendedOrg = new() { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta", Type = OrganizationType.Renter, Status = OrganizationStatus.Active };
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

        UserService service = CreateService(new RecordingUserRepository { ProfileUser = user });

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
        UserService service = CreateService(new RecordingUserRepository());

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
        IRoleRepository? roleRepository = null,
        IPasswordHasher<User>? passwordHasher = null,
        TimeProvider? timeProvider = null,
        string creationKey = ValidBootstrapKey)
    {
        return new UserService(
            userRepository,
            roleRepository ?? new FakeRoleRepository(),
            passwordHasher ?? new PasswordHasher<User>(),
            Options.Create(new AdminBootstrapOptions { CreationKey = creationKey }),
            timeProvider ?? TimeProvider.System,
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

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public Dictionary<string, Role> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.Any(r => r.Id == roleId));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.FirstOrDefault(r => r.Id == roleId));

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(name));

        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingUserRepository : IUserRepository
    {
        public string? ExistingCitizenId { get; init; }

        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(citizenId == ExistingCitizenId);

        public string? ExistingEmail { get; init; }
        public bool RejectCreate { get; init; }
        public User? ProfileUser { get; init; }
        public List<User> CreatedUsers { get; } = [];
        public List<(User User, Guid RoleId)> CreatedWithRole { get; } = [];

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

        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default)
        {
            if (RejectCreate)
            {
                throw new DuplicateEmailException();
            }

            CreatedUsers.Add(user);
            CreatedWithRole.Add((user, roleId));
            return Task.FromResult(user);
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
