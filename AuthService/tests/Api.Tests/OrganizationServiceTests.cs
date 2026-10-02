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
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

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
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = []; // Not platform admin, no membership

        Result<OrganizationResponse> result = await service.GetByIdAsync(ActorUserId, orgId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task SuspendAsyncNonPlatformAdminCannotSuspend()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [Permissions.Organizations.Update]; // NOT platform suspend admin

        Result result = await service.SuspendAsync(ActorUserId, orgId);

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
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

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

    [Fact]
    public async Task GetAdminByIdAsyncChecksPermissionsAndNotFound()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Org 1", Slug = "org-1", Status = OrganizationStatus.Active };

        // 1. Without permission -> 403
        Result<OrganizationResponse> noPerm = await service.GetAdminByIdAsync(ActorUserId, orgId);
        Assert.True(noPerm.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, noPerm.Error);

        // 2. With permission but not found -> 404
        memRepo.GlobalPermissions[ActorUserId] = [PermissionCode.IdentityOrganizationsRead];
        Result<OrganizationResponse> notFound = await service.GetAdminByIdAsync(ActorUserId, Guid.NewGuid());
        Assert.True(notFound.IsFailure);
        Assert.Equal(AuthErrors.Organization.OrganizationNotFound, notFound.Error);

        // 3. Success -> 200
        Result<OrganizationResponse> ok = await service.GetAdminByIdAsync(ActorUserId, orgId);
        Assert.True(ok.IsSuccess);
        Assert.Equal("Org 1", ok.Value.Name);
    }

    [Fact]
    public async Task UpdateNameAsyncValidatesInputAndUpdates()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Org Before", Slug = "org-before", Status = OrganizationStatus.Active };
        memRepo.GlobalPermissions[ActorUserId] = [PermissionCode.IdentityOrganizationsUpdate];

        // Empty name -> 400
        Result empty = await service.UpdateNameAsync(ActorUserId, orgId, new UpdateOrganizationRequest(""));
        Assert.True(empty.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidName, empty.Error);

        // Same name -> 204 no-op
        Result same = await service.UpdateNameAsync(ActorUserId, orgId, new UpdateOrganizationRequest("Org Before"));
        Assert.True(same.IsSuccess);

        // Rename -> 204 updated
        Result renamed = await service.UpdateNameAsync(ActorUserId, orgId, new UpdateOrganizationRequest("Org After"));
        Assert.True(renamed.IsSuccess);
        Assert.Equal("Org After", orgRepo.Organizations[orgId].Name);
    }

    [Fact]
    public async Task SuspendAndActivateAsyncValidatesStateTransitions()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, roleRepo, FakeMediaUrlResolver.Instance);

        Guid activeOrgId = Guid.NewGuid();
        Guid pendingOrgId = Guid.NewGuid();
        Guid suspendedOrgId = Guid.NewGuid();

        orgRepo.Organizations[activeOrgId] = new Organization { Id = activeOrgId, Name = "Active", Slug = "act", Status = OrganizationStatus.Active };
        orgRepo.Organizations[pendingOrgId] = new Organization { Id = pendingOrgId, Name = "Pending", Slug = "pend", Status = OrganizationStatus.Pending };
        orgRepo.Organizations[suspendedOrgId] = new Organization { Id = suspendedOrgId, Name = "Suspended", Slug = "susp", Status = OrganizationStatus.Suspended };

        memRepo.GlobalPermissions[ActorUserId] = [PermissionCode.IdentityOrganizationsSuspend];

        // Suspend Pending -> 409
        Result suspPending = await service.SuspendAsync(ActorUserId, pendingOrgId);
        Assert.True(suspPending.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidStateTransition, suspPending.Error);

        // Suspend Suspended -> 204 idempotent
        Result suspSusp = await service.SuspendAsync(ActorUserId, suspendedOrgId);
        Assert.True(suspSusp.IsSuccess);

        // Suspend Active -> 204 success
        Result suspActive = await service.SuspendAsync(ActorUserId, activeOrgId);
        Assert.True(suspActive.IsSuccess);
        Assert.Equal(OrganizationStatus.Suspended, orgRepo.Organizations[activeOrgId].Status);

        // Activate Pending -> 409
        Result actPending = await service.ActivateAsync(ActorUserId, pendingOrgId);
        Assert.True(actPending.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidStateTransition, actPending.Error);

        // Activate Active -> 204 idempotent
        orgRepo.Organizations[activeOrgId].Status = OrganizationStatus.Active;
        Result actActive = await service.ActivateAsync(ActorUserId, activeOrgId);
        Assert.True(actActive.IsSuccess);

        // Activate Suspended -> 204 success
        Result actSuspended = await service.ActivateAsync(ActorUserId, suspendedOrgId);
        Assert.True(actSuspended.IsSuccess);
        Assert.Equal(OrganizationStatus.Active, orgRepo.Organizations[suspendedOrgId].Status);
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

    internal sealed class FakeOrganizationRepository : IOrganizationRepository
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

    internal sealed class FakeMembershipRepository : IOrganizationMembershipRepository
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

    internal sealed class FakeRoleRepository : IRoleRepository
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
