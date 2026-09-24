using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class ExternalLoginServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignInAsyncAlreadyLinkedActiveUserReturnsThatUserWithoutWriting()
    {
        User user = CreateUser("len@example.com");
        FakeExternalLogins logins = new();
        logins.Links.Add(CreateLink(user, "google-sub-1"));
        FakeUsers users = new();
        ExternalLoginService service = CreateService(logins, users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: null, emailVerified: false));

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value);
        Assert.Empty(logins.Added);
        Assert.Empty(users.Created);
    }

    [Fact]
    public async Task SignInAsyncAlreadyLinkedButUserDisabledReturnsAccountDisabled()
    {
        User user = CreateUser("len@example.com");
        user.IsActive = false;
        FakeExternalLogins logins = new();
        logins.Links.Add(CreateLink(user, "google-sub-1"));
        ExternalLoginService service = CreateService(logins, new FakeUsers());

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.AccountDisabled, result.Error);
    }

    [Fact]
    public async Task SignInAsyncStillFindsUserAfterProviderEmailChanged()
    {
        User user = CreateUser("old@example.com");
        FakeExternalLogins logins = new();
        logins.Links.Add(CreateLink(user, "google-sub-1"));
        ExternalLoginService service = CreateService(logins, new FakeUsers());

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: "new-address@example.com"));

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value);
    }

    [Fact]
    public async Task SignInAsyncNotLinkedWithoutEmailReturnsMissingEmail()
    {
        FakeUsers users = new();
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: "  "));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.MissingEmail, result.Error);
        Assert.Empty(users.Created);
    }

    [Fact]
    public async Task SignInAsyncUnverifiedEmailIsRejectedAndNeverLinkedToExistingAccount()
    {
        User victim = CreateUser("len@example.com");
        FakeUsers users = new() { Existing = victim };
        FakeExternalLogins logins = new();
        ExternalLoginService service = CreateService(logins, users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("attacker-sub", email: "len@example.com", emailVerified: false));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.EmailNotVerified, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Empty(logins.Added);
        Assert.Empty(users.Created);
    }

    [Fact]
    public async Task SignInAsyncVerifiedEmailMatchingExistingUserLinksWithNormalizedEmail()
    {
        User existing = CreateUser("len@example.com");
        FakeUsers users = new() { Existing = existing };
        FakeExternalLogins logins = new();
        ExternalLoginService service = CreateService(logins, users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: "  Len@Example.COM "));

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
        Assert.Equal("len@example.com", users.LastLookupEmail);
        ExternalLogin link = Assert.Single(logins.Added);
        Assert.Equal(existing.Id, link.UserId);
        Assert.Equal("Google", link.Provider);
        Assert.Equal("google-sub-1", link.ProviderSubject);
        Assert.Equal("len@example.com", link.Email);
        Assert.Equal(Now.UtcDateTime, link.CreatedAtUtc);
        Assert.Empty(users.Created);
    }

    [Fact]
    public async Task SignInAsyncVerifiedEmailMatchingDisabledUserReturnsAccountDisabled()
    {
        User existing = CreateUser("len@example.com");
        existing.IsActive = false;
        FakeExternalLogins logins = new();
        ExternalLoginService service = CreateService(logins, new FakeUsers { Existing = existing });

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: "len@example.com"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.AccountDisabled, result.Error);
        Assert.Empty(logins.Added);
    }

    [Fact]
    public async Task SignInAsyncConcurrentLinkOfSameAccountReturnsExistingUser()
    {
        User existing = CreateUser("len@example.com");
        FakeExternalLogins logins = new() { RejectAdd = true, LinkVisibleAfterRace = CreateLink(existing, "google-sub-1") };
        ExternalLoginService service = CreateService(logins, new FakeUsers { Existing = existing });

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-1", email: "len@example.com"));

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
    }

    [Fact]
    public async Task SignInAsyncUserAlreadyLinkedToAnotherGoogleAccountReturnsConflict()
    {
        User existing = CreateUser("len@example.com");
        FakeExternalLogins logins = new() { RejectAdd = true };
        ExternalLoginService service = CreateService(logins, new FakeUsers { Existing = existing });

        Result<Guid> result = await service.SignInAsync(CreateIdentity("second-google-sub", email: "len@example.com"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.ExternalLogin.AlreadyLinked, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task SignInAsyncNewUserIsCreatedActiveWithLinkAndUnusablePassword()
    {
        FakeUsers users = new();
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-9", email: "An@Example.com", fullName: "  An Nguyen "));

        Assert.True(result.IsSuccess);
        User created = Assert.Single(users.Created);
        Assert.Equal(result.Value, created.Id);
        Assert.Equal("an@example.com", created.Email);
        Assert.Equal("An Nguyen", created.FullName);
        Assert.True(created.IsActive);
        Assert.Equal(Now.UtcDateTime, created.CreatedAt);
        ExternalLogin link = Assert.Single(created.ExternalLogins);
        Assert.Equal(created.Id, link.UserId);
        Assert.Equal("google-sub-9", link.ProviderSubject);

        PasswordHasher<User> hasher = new();
        Assert.NotEmpty(created.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(created, created.PasswordHash, string.Empty));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(created, created.PasswordHash, "Matkhau123!"));
    }

    [Fact]
    public async Task SignInAsyncNewUserIsAssignedCustomerRoleWhenSeeded()
    {
        FakeUsers users = new();
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-10", email: "role-test@example.com"));

        Assert.True(result.IsSuccess);
        Assert.Single(users.Created);
        var (assignedUser, assignedRoleId) = Assert.Single(users.CreatedWithRole);
        Assert.Equal(result.Value, assignedUser.Id);
        Assert.Equal(CustomerRoleId, assignedRoleId);
    }

    [Fact]
    public async Task SignInAsyncNewUserWithoutSeededCustomerRoleStillCreatesAccount()
    {
        FakeUsers users = new();
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users, new FakeRoles());

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-11", email: "no-role@example.com"));

        Assert.True(result.IsSuccess);
        Assert.Single(users.Created);
        Assert.Empty(users.CreatedWithRole);
    }

    [Fact]
    public async Task SignInAsyncNewUserWithoutNameUsesEmailAndTruncatesLongNames()
    {
        FakeUsers users = new();
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users);

        await service.SignInAsync(CreateIdentity("sub-a", email: "no-name@example.com", fullName: null));
        await service.SignInAsync(CreateIdentity("sub-b", email: "long-name@example.com", fullName: new string('x', 250)));

        Assert.Equal("no-name@example.com", users.Created[0].FullName);
        Assert.Equal(100, users.Created[1].FullName.Length);
    }

    [Fact]
    public async Task SignInAsyncConcurrentCreateReturnsUserCreatedByTheOtherRequest()
    {
        User winner = CreateUser("an@example.com");
        FakeExternalLogins logins = new() { LinkVisibleAfterRace = CreateLink(winner, "google-sub-9") };
        FakeUsers users = new() { RejectCreate = true, OnCreateRejected = () => logins.RaceHappened = true };
        ExternalLoginService service = CreateService(logins, users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-9", email: "an@example.com"));

        Assert.True(result.IsSuccess);
        Assert.Equal(winner.Id, result.Value);
    }

    [Fact]
    public async Task SignInAsyncEmailTakenByRegistrationRaceWithoutLinkReturnsConflict()
    {
        FakeUsers users = new() { RejectCreate = true };
        ExternalLoginService service = CreateService(new FakeExternalLogins(), users);

        Result<Guid> result = await service.SignInAsync(CreateIdentity("google-sub-9", email: "an@example.com"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists, result.Error);
    }

    private static readonly Guid CustomerRoleId = Guid.NewGuid();

    private static ExternalLoginService CreateService(FakeExternalLogins logins, FakeUsers users, FakeRoles? roles = null) =>
        new(logins, users, roles ?? CreateRolesWithCustomerSeeded(), new PasswordHasher<User>(), new FixedTimeProvider(Now));

    private static FakeRoles CreateRolesWithCustomerSeeded()
    {
        FakeRoles roles = new();
        roles.Roles[SystemRoleCode.Customer] = new Role { Id = CustomerRoleId, Name = SystemRoleCode.Customer, Scope = RoleScope.Global };
        return roles;
    }

    private static ExternalIdentity CreateIdentity(
        string subject,
        string? email = "len@example.com",
        bool emailVerified = true,
        string? fullName = "Huynh Tan Len") =>
        new("Google", subject, email, emailVerified, fullName);

    private static User CreateUser(string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Existing User",
            PasswordHash = "hash",
            IsActive = true,
        };

    private static ExternalLogin CreateLink(User user, string subject) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            User = user,
            Provider = "Google",
            ProviderSubject = subject,
        };

    private sealed class FakeExternalLogins : IExternalLoginRepository
    {
        public List<ExternalLogin> Links { get; } = [];
        public List<ExternalLogin> Added { get; } = [];
        public bool RejectAdd { get; init; }
        public bool RaceHappened { get; set; }
        public ExternalLogin? LinkVisibleAfterRace { get; init; }

        public Task<ExternalLogin?> FindAsync(string provider, string providerSubject, CancellationToken cancellationToken = default)
        {
            ExternalLogin? link = Links.SingleOrDefault(candidate => candidate.Provider == provider && candidate.ProviderSubject == providerSubject);
            if (link is null && RaceHappened)
            {
                link = LinkVisibleAfterRace;
            }

            return Task.FromResult(link);
        }

        public Task AddAsync(ExternalLogin login, CancellationToken cancellationToken = default)
        {
            if (RejectAdd)
            {
                RaceHappened = true;
                throw new DuplicateExternalLoginException();
            }

            Added.Add(login);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExternalLogin>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalLogin>>(Links.Where(l => l.UserId == userId).ToList());

        public Task<bool> DeleteAsync(Guid userId, string provider, CancellationToken cancellationToken = default)
        {
            int removed = Links.RemoveAll(l => l.UserId == userId && l.Provider == provider);
            return Task.FromResult(removed > 0);
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public User? Existing { get; init; }
        public bool RejectCreate { get; init; }
        public Action? OnCreateRejected { get; init; }
        public List<User> Created { get; } = [];
        public List<(User User, Guid RoleId)> CreatedWithRole { get; } = [];
        public string? LastLookupEmail { get; private set; }

        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        {
            LastLookupEmail = normalizedEmail;
            return Task.FromResult(Existing is not null && Existing.Email == normalizedEmail ? Existing : null);
        }
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not use pagination.");
        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
        {
            if (RejectCreate)
            {
                OnCreateRejected?.Invoke();
                throw new DuplicateEmailException();
            }

            Created.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default)
        {
            if (RejectCreate)
            {
                OnCreateRejected?.Invoke();
                throw new DuplicateEmailException();
            }

            Created.Add(user);
            CreatedWithRole.Add((user, roleId));
            return Task.FromResult(user);
        }

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not check email existence separately.");

        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not read profiles.");

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not use generic CRUD reads.");

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not use generic CRUD reads.");

        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not update users.");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not delete users.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeRoles : IRoleRepository
    {
        public Dictionary<string, Role> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(name));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not look up roles by id.");

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not check role existence.");

        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not read role permissions.");

        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not read user permissions.");

        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not check role assignment.");

        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not assign roles directly.");

        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ExternalLoginService does not revoke roles.");
    }
}
