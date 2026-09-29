using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;


using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class OrganizationServiceTests
{
    private static readonly Guid ActorAdminId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

    [Fact]
    public async Task GetByIdAsyncInactiveOrganizationReturnsForbiddenForNormalUser()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Inactive", Slug = "inactive", Status = OrganizationStatus.Suspended };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [];

        Result<OrganizationResponse> result = await service.GetByIdAsync(ActorUserId, orgId);

        Assert.True(result.IsFailure);
        // Người ngoài nhận cùng lỗi với tổ chức Active — không lộ trạng thái tổ chức.
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task GetByIdAsyncCrossOrganizationRequestReturnsForbidden()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = []; // Not platform admin, no membership

        Result<OrganizationResponse> result = await service.GetByIdAsync(ActorUserId, orgId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task UpdateAsyncNonPlatformAdminCannotChangeIsActive()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [Permissions.Organizations.Update]; // NOT platform admin

        Result result = await service.UpdateAsync(
            ActorUserId,
            orgId,
            new UpdateOrganizationRequest(IsActive: false));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, result.Error);
    }

    [Fact]
    public async Task GetMyOrganizationsAsyncReturnsActiveMembershipsOnly()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        Guid userId = Guid.NewGuid();
        Guid activeOrgId = Guid.NewGuid();
        Guid invitedOrgId = Guid.NewGuid();

        Organization activeOrg = new() { Id = activeOrgId, Name = "Active Co", Slug = "active-co", Status = OrganizationStatus.Active };
        Organization invitedOrg = new() { Id = invitedOrgId, Name = "Invited Co", Slug = "invited-co", Status = OrganizationStatus.Active };
        Role memberRole = new() { Id = Guid.NewGuid(), Name = "Member" };

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = activeOrgId,
            Organization = activeOrg,
            Role = memberRole,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        });
        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = invitedOrgId,
            Organization = invitedOrg,
            Role = memberRole,
            Status = MembershipStatus.Invited,
            JoinedAtUtc = DateTime.UtcNow
        });

        Result<IReadOnlyList<MyOrganizationResponse>> result = await service.GetMyOrganizationsAsync(userId);

        Assert.True(result.IsSuccess);
        MyOrganizationResponse item = Assert.Single(result.Value);
        Assert.Equal(activeOrgId, item.OrganizationId);
        Assert.Equal("Active Co", item.Name);
        Assert.Equal("Member", item.RoleName);
    }

    private static readonly string[] AdminPermissions =
[
    PermissionCode.IdentityOrganizationsSuspend,
        PermissionCode.IdentityOrganizationsCreate,
        PermissionCode.IdentityOrganizationsRead,
        PermissionCode.IdentityOrganizationsUpdate,
        PermissionCode.IdentityMembersRead,
        PermissionCode.IdentityMembersManage,
        PermissionCode.IdentityRolesAssignOrganization,
        PermissionCode.IdentityRolesRead
];

    private sealed class FakeOrganizationRepository : IOrganizationRepository
    {
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);


        public Dictionary<Guid, Organization> Organizations { get; } = [];
        public HashSet<string> ExistingSlugs { get; } = [];
        public OrganizationMembership? LastCreatedInitialMembership { get; private set; }

        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Organizations.GetValueOrDefault(id));

        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(Organizations.Values.FirstOrDefault(o => o.Slug == slug));

        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingSlugs.Contains(slug) || Organizations.Values.Any(o => o.Slug == slug));

        public Task<Organization?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken = default) =>
            GetByIdAsync(id, cancellationToken);

        public Task<PagedResult<Organization>> GetAllAsync(PageRequest request, OrganizationStatus? status = null, CancellationToken cancellationToken = default)
        {
            List<Organization> list = Organizations.Values
                .Where(o => status is null || o.Status == status)
                .OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).ToList();
            List<Organization> paged = list.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(PagedResult.Create(paged, request.Page, request.PageSize, list.Count));
        }

        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default)
        {
            if (ExistingSlugs.Contains(organization.Slug))
            {
                throw new StorageConstraintViolationException(StorageConstraintViolationType.Unique, StorageConstraints.OrganizationSlug);
            }

            Organizations[organization.Id] = organization;
            LastCreatedInitialMembership = initialMembership;
            return Task.FromResult(organization);
        }


        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
        {
            Organizations[organization.Id] = organization;
            return Task.FromResult(true);
        }
        public Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult(IExecutionTransaction.None);
    }

    private sealed class FakeMembershipRepository : IOrganizationMembershipRepository
    {
        public List<OrganizationMembership> Memberships { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> GlobalPermissions { get; } = [];
        public Dictionary<(Guid, Guid), IReadOnlyList<string>> EffectivePermissions { get; } = [];

        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.OrganizationId == organizationId && m.UserId == userId));

        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Count(m => m.OrganizationId == organizationId && string.Equals(m.Role?.Name, "Owner", StringComparison.OrdinalIgnoreCase) && m.Status == MembershipStatus.Active));

        public Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrganizationMembership>>(Memberships.Where(m => m.UserId == userId).ToList());
        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobalPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
        public Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectivePermissions.GetValueOrDefault((userId, organizationId)) ?? (IReadOnlyList<string>)[]);
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Dictionary<Guid, User> Users { get; } = [];

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Users.GetValueOrDefault(id));

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(Users.Values.ToList());

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default) => Task.FromResult(entity);
        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(id));

        public Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(id));

        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, string? type, CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyCollection<User> Items, int TotalCount)>((Users.Values.Skip(skip).Take(take).ToList(), Users.Count));

        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyCollection<User> Items, int TotalCount)>((Users.Values.Skip(skip).Take(take).ToList(), Users.Count));
        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Users.TryGetValue(userId, out var u) && u.IsActive);
    }

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public Dictionary<Guid, Role> Roles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> RolePermissions { get; } = [];

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.ContainsKey(roleId));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(roleId));

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RolePermissions.GetValueOrDefault(roleId) ?? (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Role>>(Roles.Values.ToList());
        }
        public Task<int> CountByIdsAsync(IEnumerable<Guid> roleIds, CancellationToken cancellationToken = default)
        {
            int count = Roles.Keys.Count(id => roleIds.Contains(id));
            return Task.FromResult(count);
        }
    }

}
