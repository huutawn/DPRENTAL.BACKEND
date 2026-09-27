using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;

using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class AuthorizationMatrixTests
{
    private static readonly Guid SuperAdminUserId = Guid.NewGuid();
    private static readonly Guid PlatformAdminUserId = Guid.NewGuid();
    private static readonly Guid OwnerUserId = Guid.NewGuid();
    private static readonly Guid MemberUserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid GlobalRoleId = Guid.NewGuid();
    private static readonly Guid OrgOwnerRoleId = Guid.NewGuid();
    private static readonly Guid OrgMemberRoleId = Guid.NewGuid();

    private readonly FakeOrgRepo _orgRepo = new();
    private readonly FakeMemRepo _memRepo = new();
    private readonly FakeUserRepo _userRepo = new();
    private readonly FakeRoleRepo _roleRepo = new();

    public AuthorizationMatrixTests()
    {
        // Setup Roles
        _roleRepo.Roles[GlobalRoleId] = new Role { Id = GlobalRoleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        _roleRepo.RolePermissions[GlobalRoleId] = [PermissionCode.IdentityUsersRead];

        _roleRepo.Roles[OrgOwnerRoleId] = new Role { Id = OrgOwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        _roleRepo.RolePermissions[OrgOwnerRoleId] =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityOrganizationsUpdate,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityMembersManage,
            PermissionCode.IdentityRolesRead,
            PermissionCode.IdentityRolesAssignOrganization,
            PermissionCode.IdentityRolesRevokeOrganization
        ];

        _roleRepo.Roles[OrgMemberRoleId] = new Role { Id = OrgMemberRoleId, Name = "Member", Scope = RoleScope.Organization };
        _roleRepo.RolePermissions[OrgMemberRoleId] =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityRolesRead
        ];

        // Setup Users
        _userRepo.Users[SuperAdminUserId] = new User { Id = SuperAdminUserId, IsActive = true, Email = "super@example.com" };
        _userRepo.Users[PlatformAdminUserId] = new User { Id = PlatformAdminUserId, IsActive = true, Email = "platform@example.com" };
        _userRepo.Users[OwnerUserId] = new User { Id = OwnerUserId, IsActive = true, Email = "owner@example.com" };
        _userRepo.Users[MemberUserId] = new User { Id = MemberUserId, IsActive = true, Email = "member@example.com" };

        // Setup Organization
        _orgRepo.Organizations[OrganizationId] = new Organization
        {
            Id = OrganizationId,
            Name = "Matrix Org",
            Slug = "matrix-org",
            Type = OrganizationType.Supplier,
            Status = OrganizationStatus.Active
        };

        // Super Admin permissions (all 17)
        _memRepo.GlobalPermissions[SuperAdminUserId] = [.. PermissionCode.All];
        _roleRepo.UserPermissions[SuperAdminUserId] = [.. PermissionCode.All];

        // Platform Admin permissions (all 15 except global assign/revoke)
        List<string> platformPerms = PermissionCode.All
            .Where(p => p != PermissionCode.IdentityRolesAssignGlobal && p != PermissionCode.IdentityRolesRevokeGlobal)
            .ToList();
        _memRepo.GlobalPermissions[PlatformAdminUserId] = platformPerms;
        _roleRepo.UserPermissions[PlatformAdminUserId] = platformPerms;

        // Owner: Active member in OrganizationId with Owner role
        _memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            UserId = OwnerUserId,
            RoleId = OrgOwnerRoleId,
            Status = MembershipStatus.Active
        });
        _memRepo.EffectivePermissions[(OwnerUserId, OrganizationId)] =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityOrganizationsUpdate,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityMembersManage,
            PermissionCode.IdentityRolesRead,
            PermissionCode.IdentityRolesAssignOrganization,
            PermissionCode.IdentityRolesRevokeOrganization
        ];

        // Member: Active member in OrganizationId with Member role
        _memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            UserId = MemberUserId,
            RoleId = OrgMemberRoleId,
            Status = MembershipStatus.Active
        });
        _memRepo.EffectivePermissions[(MemberUserId, OrganizationId)] =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityRolesRead
        ];
    }

    [Fact]
    public async Task SuperAdminHasFullAuthorizationAcrossPlatformAndGlobalRoles()
    {
        OrganizationService orgService = new(_orgRepo, _memRepo, _userRepo, _roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);
        RoleAssignmentService roleService = new(_roleRepo, _memRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleAssignmentService>.Instance);

        // 1. Can suspend/activate org
        Result suspendRes = await orgService.UpdateAsync(SuperAdminUserId, OrganizationId, new UpdateOrganizationRequest(IsActive: false));
        Assert.True(suspendRes.IsSuccess);

        // 2. Can assign global role
        Guid target = Guid.NewGuid();
        Result assignRes = await roleService.AssignAsync(SuperAdminUserId, new AssignRoleRequest(target, GlobalRoleId));
        Assert.True(assignRes.IsSuccess);

        // 3. Can revoke global role
        Result revokeRes = await roleService.RevokeAsync(SuperAdminUserId, new AssignRoleRequest(target, GlobalRoleId));
        Assert.True(revokeRes.IsSuccess);
    }

    [Fact]
    public async Task PlatformAdminCanManageOrganizationsButCannotAssignOrRevokeGlobalRoles()
    {
        OrganizationService orgService = new(_orgRepo, _memRepo, _userRepo, _roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);
        RoleAssignmentService roleService = new(_roleRepo, _memRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleAssignmentService>.Instance);

        // 1. Can suspend/activate org (has identity.organizations.suspend)
        Result suspendRes = await orgService.UpdateAsync(PlatformAdminUserId, OrganizationId, new UpdateOrganizationRequest(IsActive: false));
        Assert.True(suspendRes.IsSuccess);

        // 2. CANNOT assign global role (lacks identity.roles.assign.global)
        Guid target = Guid.NewGuid();
        Result assignRes = await roleService.AssignAsync(PlatformAdminUserId, new AssignRoleRequest(target, GlobalRoleId));
        Assert.True(assignRes.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, assignRes.Error);

        // 3. CANNOT revoke global role (lacks identity.roles.revoke.global)
        Result revokeRes = await roleService.RevokeAsync(PlatformAdminUserId, new AssignRoleRequest(target, GlobalRoleId));
        Assert.True(revokeRes.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, revokeRes.Error);
    }

    [Fact]
    public async Task OwnerCannotRenameSuspendOrgOrAssignGlobalRoles()
    {
        OrganizationService orgService = new(_orgRepo, _memRepo, _userRepo, _roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);
        RoleAssignmentService roleService = new(_roleRepo, _memRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleAssignmentService>.Instance);

        // 1. CANNOT rename org — tên là thông tin pháp lý đã được Platform xác minh
        Result nameRes = await orgService.UpdateAsync(OwnerUserId, OrganizationId, new UpdateOrganizationRequest(Name: "Renamed Org"));
        Assert.True(nameRes.IsFailure);
        Assert.Equal(AuthErrors.Organization.LegalInfoLocked, nameRes.Error);

        // 2. CANNOT suspend/activate org (lacks identity.organizations.suspend)
        Result suspendRes = await orgService.UpdateAsync(OwnerUserId, OrganizationId, new UpdateOrganizationRequest(IsActive: false));
        Assert.True(suspendRes.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, suspendRes.Error);

        // 3. CANNOT assign global role (no global permissions)
        Guid target = Guid.NewGuid();
        Result assignRes = await roleService.AssignAsync(OwnerUserId, new AssignRoleRequest(target, GlobalRoleId));
        Assert.True(assignRes.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, assignRes.Error);
    }

    [Fact]
    public async Task OwnerCanAssignOrganizationScopedRoleWithinOwnOrganization()
    {
        RoleAssignmentService roleService = new(_roleRepo, _memRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleAssignmentService>.Instance);

        Guid newMemberTarget = Guid.NewGuid();
        _memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            UserId = newMemberTarget,
            RoleId = OrgMemberRoleId,
            Status = MembershipStatus.Active
        });

        Result result = await roleService.AssignAsync(OwnerUserId, new AssignRoleRequest(newMemberTarget, OrgOwnerRoleId, OrganizationId));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrgOwnerRoleId, _memRepo.Memberships.Single(m => m.UserId == newMemberTarget).RoleId);
    }

    [Fact]
    public async Task OwnerCannotAssignOrganizationScopedRoleInADifferentOrganization()
    {
        // Acceptance criterion DPR-107: user ở Supplier A không thể dùng role của mình quản lý Supplier B.
        RoleAssignmentService roleService = new(_roleRepo, _memRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleAssignmentService>.Instance);

        Guid supplierBOrganizationId = Guid.NewGuid();
        Guid supplierBTarget = Guid.NewGuid();
        _memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = supplierBOrganizationId,
            UserId = supplierBTarget,
            RoleId = OrgMemberRoleId,
            Status = MembershipStatus.Active
        });

        // OwnerUserId chỉ là Owner của OrganizationId (Supplier A) — không có membership ở supplierBOrganizationId.
        Result result = await roleService.AssignAsync(OwnerUserId, new AssignRoleRequest(supplierBTarget, OrgOwnerRoleId, supplierBOrganizationId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task MemberCanReadOrgAndMembersButCannotUpdateOrgOrManageMembers()
    {
        OrganizationService orgService = new(_orgRepo, _memRepo, _userRepo, _roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        // 1. Can read org
        Result<OrganizationResponse> getRes = await orgService.GetByIdAsync(MemberUserId, OrganizationId);
        Assert.True(getRes.IsSuccess);

        // 2. Can read members
        Result<PagedResult<OrganizationMemberResponse>> membersRes = await orgService.GetMembersAsync(MemberUserId, OrganizationId, new PageRequest());
        Assert.True(membersRes.IsSuccess);

        // 3. CANNOT update org name
        Result nameRes = await orgService.UpdateAsync(MemberUserId, OrganizationId, new UpdateOrganizationRequest(Name: "Member Renamed"));
        Assert.True(nameRes.IsFailure);
        Assert.Equal(AuthErrors.Organization.Forbidden, nameRes.Error);

        // 4. CANNOT suspend org
        Result suspendRes = await orgService.UpdateAsync(MemberUserId, OrganizationId, new UpdateOrganizationRequest(IsActive: false));
        Assert.True(suspendRes.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, suspendRes.Error);

        // 5. CANNOT add member
        Guid newTarget = Guid.NewGuid();
        _userRepo.Users[newTarget] = new User { Id = newTarget, IsActive = true };
        Result addMemRes = await orgService.AddMemberAsync(MemberUserId, OrganizationId, new AddOrganizationMemberRequest(newTarget, OrgMemberRoleId));
        Assert.True(addMemRes.IsFailure);
        Assert.Equal(AuthErrors.Organization.Forbidden, addMemRes.Error);
    }

    private sealed class FakeOrgRepo : IOrganizationRepository
    {
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);


        public Dictionary<Guid, Organization> Organizations { get; } = [];
        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Organizations.GetValueOrDefault(id));
        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(Organizations.Values.FirstOrDefault(o => o.Slug == slug));
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<PagedResult<Organization>> GetAllAsync(PageRequest request, OrganizationStatus? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<Organization>([.. Organizations.Values], request.Page, request.PageSize, Organizations.Count));
        public Task<Organization?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => GetByIdAsync(id, cancellationToken);
        public Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<Organization>([.. Organizations.Values], request.Page, request.PageSize, Organizations.Count));
        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default) =>
            Task.FromResult(organization);

        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
        {
            Organizations[organization.Id] = organization;
            return Task.FromResult(true);
        }
        public Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult(IExecutionTransaction.None);
    }

    private sealed class FakeMemRepo : IOrganizationMembershipRepository
    {
        public List<OrganizationMembership> Memberships { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> GlobalPermissions { get; } = [];
        public Dictionary<(Guid, Guid), IReadOnlyList<string>> EffectivePermissions { get; } = [];

        public Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.Id == id));
        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.OrganizationId == organizationId && m.UserId == userId));
        public Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.OrganizationId == organizationId && (m.Id == memberOrUserId || m.UserId == memberOrUserId)));
        public Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<OrganizationMembership>(Memberships.Where(m => m.OrganizationId == organizationId).ToList(), request.Page, request.PageSize, Memberships.Count));
        public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Any(m => m.OrganizationId == organizationId && m.UserId == userId));
        public Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
        {
            Memberships.Add(membership);
            return Task.FromResult(membership);
        }
        public Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectivePermissions.GetValueOrDefault((userId, organizationId)) ?? (IReadOnlyList<string>)[]);
        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobalPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
        public Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrganizationMembership>>(Memberships.Where(m => m.UserId == userId).ToList());
    }

    private sealed class FakeUserRepo : IUserRepository
    {
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Dictionary<Guid, User> Users { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(id));
        public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<User>>([]);
        public Task<bool> UpdateAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyCollection<User> Items, int TotalCount)>((Users.Values.Skip(skip).Take(take).ToList(), Users.Count));
        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Users.TryGetValue(userId, out var u) && u.IsActive);
    }

    private sealed class FakeRoleRepo : IRoleRepository
    {
        public Dictionary<Guid, Role> Roles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> RolePermissions { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> UserPermissions { get; } = [];
        public HashSet<(Guid, Guid)> Assigned { get; } = [];

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(Roles.ContainsKey(roleId));
        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(Roles.GetValueOrDefault(roleId));
        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Roles.Values.FirstOrDefault(r => r.Name == name));
        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RolePermissions.GetValueOrDefault(roleId) ?? (IReadOnlyList<string>)[]);
        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Assigned.Contains((userId, roleId)));
        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
        {
            Assigned.Add((userId, roleId));
            return Task.FromResult(true);
        }
        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
        {
            Assigned.Remove((userId, roleId));
            return Task.FromResult(true);
        }
    }
}
