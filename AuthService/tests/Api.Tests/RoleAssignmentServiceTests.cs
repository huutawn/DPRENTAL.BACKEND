using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.IRepositories;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class RoleAssignmentServiceTests
{
    [Fact]
    public async Task AssignAsyncRejectsOrganizationScopedRoleWithoutOrganizationId()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid orgRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[orgRoleId] = new Role { Id = orgRoleId, Name = "OrgOwner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[orgRoleId] = ["perm.one"];
        roleRepo.UserPermissions[actorUserId] = ["perm.one"];

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, orgRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.OrganizationRequired, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task RevokeAsyncRejectsOrganizationScopedRoleWithoutOrganizationId()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid orgRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[orgRoleId] = new Role { Id = orgRoleId, Name = "OrgOwner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[orgRoleId] = ["perm.one"];
        roleRepo.UserPermissions[actorUserId] = ["perm.one"];

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.RevokeAsync(actorUserId, new AssignRoleRequest(targetUserId, orgRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InvalidScope, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task RevokeAsyncRejectsOrganizationScopedRoleWithOrganizationId()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid orgRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[orgRoleId] = new Role { Id = orgRoleId, Name = "OrgOwner", Scope = RoleScope.Organization };

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.RevokeAsync(actorUserId, new AssignRoleRequest(targetUserId, orgRoleId, organizationId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.OrganizationRevokeNotSupported, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task AssignAsyncSucceedsForGlobalRole()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[globalRoleId] = new Role { Id = globalRoleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        roleRepo.RolePermissions[globalRoleId] = ["perm.one"];
        roleRepo.UserPermissions[actorUserId] = ["perm.one", PermissionCode.IdentityRolesAssignGlobal];

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsSuccess);
        Assert.Contains((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    [Fact]
    public async Task AssignAsyncRejectsPlatformAdminBecauseLacksGlobalAssignPermission()
    {
        Guid platformAdminUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[globalRoleId] = new Role { Id = globalRoleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        roleRepo.RolePermissions[globalRoleId] = [PermissionCode.IdentityUsersRead];
        // Platform Admin has all operational permissions EXCEPT identity.roles.assign.global and identity.roles.revoke.global
        roleRepo.UserPermissions[platformAdminUserId] =
        [
            PermissionCode.IdentityUsersRead,
            PermissionCode.IdentityUsersManage,
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityOrganizationsCreate,
            PermissionCode.IdentityOrganizationsUpdate,
            PermissionCode.IdentityOrganizationsSuspend,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityMembersManage,
            PermissionCode.IdentityRolesRead,
            PermissionCode.IdentityRolesAssignOrganization,
            PermissionCode.IdentityRolesRevokeOrganization
        ];

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.AssignAsync(platformAdminUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.DoesNotContain((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    [Fact]
    public async Task RevokeAsyncRejectsPlatformAdminBecauseLacksGlobalRevokePermission()
    {
        Guid platformAdminUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[globalRoleId] = new Role { Id = globalRoleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        roleRepo.RolePermissions[globalRoleId] = [PermissionCode.IdentityUsersRead];
        roleRepo.UserPermissions[platformAdminUserId] = [PermissionCode.IdentityUsersRead];
        roleRepo.Assigned.Add((targetUserId, globalRoleId));

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.RevokeAsync(platformAdminUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Contains((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    [Fact]
    public async Task RevokeAsyncSucceedsForSuperAdminWithGlobalRevokePermission()
    {
        Guid superAdminUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[globalRoleId] = new Role { Id = globalRoleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        roleRepo.RolePermissions[globalRoleId] = [PermissionCode.IdentityUsersRead];
        roleRepo.UserPermissions[superAdminUserId] = [PermissionCode.IdentityUsersRead, PermissionCode.IdentityRolesRevokeGlobal];
        roleRepo.Assigned.Add((targetUserId, globalRoleId));

        RoleAssignmentService service = new(roleRepo, new FakeOrganizationMembershipRepository());

        Result result = await service.RevokeAsync(superAdminUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    [Fact]
    public async Task AssignAsyncSucceedsForOrganizationScopedRoleWhenActorHasOrgPermission()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid ownerRoleId = Guid.NewGuid();
        Guid memberRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[ownerRoleId] = new Role { Id = ownerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[ownerRoleId] = [PermissionCode.IdentityMembersManage];

        FakeOrganizationMembershipRepository membershipRepo = new();
        membershipRepo.Memberships[(organizationId, actorUserId)] = CreateMembership(organizationId, actorUserId, ownerRoleId);
        membershipRepo.Memberships[(organizationId, targetUserId)] = CreateMembership(organizationId, targetUserId, memberRoleId);
        membershipRepo.EffectivePermissions[(actorUserId, organizationId)] =
            [PermissionCode.IdentityRolesAssignOrganization, PermissionCode.IdentityMembersManage];

        RoleAssignmentService service = new(roleRepo, membershipRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, ownerRoleId, organizationId));

        Assert.True(result.IsSuccess);
        Assert.Equal(ownerRoleId, membershipRepo.Memberships[(organizationId, targetUserId)].RoleId);
    }

    [Fact]
    public async Task AssignAsyncSucceedsForOrganizationScopedRoleViaGlobalOverride()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid ownerRoleId = Guid.NewGuid();
        Guid memberRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[ownerRoleId] = new Role { Id = ownerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[ownerRoleId] = [PermissionCode.IdentityMembersManage];

        FakeOrganizationMembershipRepository membershipRepo = new();
        // Actor không phải member của organization này — dựa hoàn toàn vào permission toàn cục.
        membershipRepo.Memberships[(organizationId, targetUserId)] = CreateMembership(organizationId, targetUserId, memberRoleId);
        membershipRepo.GlobalPermissions[actorUserId] = [PermissionCode.IdentityRolesAssignOrganization, PermissionCode.IdentityMembersManage];

        RoleAssignmentService service = new(roleRepo, membershipRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, ownerRoleId, organizationId));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AssignAsyncRejectsOrganizationScopedRoleWhenActorNotMemberOfThatOrganization()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid otherOrganizationId = Guid.NewGuid();
        Guid ownerRoleId = Guid.NewGuid();
        Guid memberRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[ownerRoleId] = new Role { Id = ownerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[ownerRoleId] = [PermissionCode.IdentityMembersManage];

        FakeOrganizationMembershipRepository membershipRepo = new();
        // Actor là Owner nhưng ở MỘT organization khác — không phải organization bị thao tác.
        membershipRepo.Memberships[(otherOrganizationId, actorUserId)] = CreateMembership(otherOrganizationId, actorUserId, ownerRoleId);
        membershipRepo.Memberships[(organizationId, targetUserId)] = CreateMembership(organizationId, targetUserId, memberRoleId);
        membershipRepo.EffectivePermissions[(actorUserId, otherOrganizationId)] = [PermissionCode.IdentityRolesAssignOrganization];

        RoleAssignmentService service = new(roleRepo, membershipRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, ownerRoleId, organizationId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task AssignAsyncRejectsOrganizationScopedRoleOnPrivilegeEscalation()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid limitedRoleId = Guid.NewGuid();
        Guid powerfulRoleId = Guid.NewGuid();
        Guid memberRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[limitedRoleId] = new Role { Id = limitedRoleId, Name = "LimitedAdmin", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[limitedRoleId] = [PermissionCode.IdentityMembersManage];
        roleRepo.Roles[powerfulRoleId] = new Role { Id = powerfulRoleId, Name = "SuperOwner", Scope = RoleScope.Organization };
        // Role được yêu cầu gán mang permission mà actor không có.
        roleRepo.RolePermissions[powerfulRoleId] = [PermissionCode.IdentityMembersManage, PermissionCode.IdentityOrganizationsSuspend];

        FakeOrganizationMembershipRepository membershipRepo = new();
        membershipRepo.Memberships[(organizationId, actorUserId)] = CreateMembership(organizationId, actorUserId, limitedRoleId);
        membershipRepo.Memberships[(organizationId, targetUserId)] = CreateMembership(organizationId, targetUserId, memberRoleId);
        membershipRepo.EffectivePermissions[(actorUserId, organizationId)] =
            [PermissionCode.IdentityRolesAssignOrganization, PermissionCode.IdentityMembersManage];

        RoleAssignmentService service = new(roleRepo, membershipRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, powerfulRoleId, organizationId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PrivilegeEscalation, result.Error);
    }

    [Fact]
    public async Task AssignAsyncRejectsOrganizationScopedRoleWhenTargetIsNotAMember()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid ownerRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[ownerRoleId] = new Role { Id = ownerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[ownerRoleId] = [];

        FakeOrganizationMembershipRepository membershipRepo = new();
        membershipRepo.GlobalPermissions[actorUserId] = [PermissionCode.IdentityRolesAssignGlobal];

        RoleAssignmentService service = new(roleRepo, membershipRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, ownerRoleId, organizationId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.MembershipNotFound, result.Error);
    }

    private static OrganizationMembership CreateMembership(Guid organizationId, Guid userId, Guid roleId, MembershipStatus status = MembershipStatus.Active) =>
        new() { Id = Guid.NewGuid(), OrganizationId = organizationId, UserId = userId, RoleId = roleId, Status = status, JoinedAtUtc = DateTime.UtcNow };

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public Dictionary<Guid, Role> Roles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> RolePermissions { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> UserPermissions { get; } = [];
        public HashSet<(Guid, Guid)> Assigned { get; } = [];

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.ContainsKey(roleId));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(roleId));

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));

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

    private sealed class FakeOrganizationMembershipRepository : IOrganizationMembershipRepository
    {
        public Dictionary<(Guid OrganizationId, Guid UserId), OrganizationMembership> Memberships { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> GlobalPermissions { get; } = [];
        public Dictionary<(Guid UserId, Guid OrganizationId), IReadOnlyList<string>> EffectivePermissions { get; } = [];

        public Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Values.FirstOrDefault(m => m.Id == id));

        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.GetValueOrDefault((organizationId, userId)));

        public Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Values.FirstOrDefault(m => m.OrganizationId == organizationId && (m.Id == memberOrUserId || m.UserId == memberOrUserId)));

        public Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Không dùng trong test RoleAssignmentService.");

        public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.ContainsKey((organizationId, userId)));

        public Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
        {
            Memberships[(membership.OrganizationId, membership.UserId)] = membership;
            return Task.FromResult(membership);
        }

        public Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Values.Any(m => m.Id == membership.Id));

        public Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Không dùng trong test RoleAssignmentService.");

        public Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Không dùng trong test RoleAssignmentService.");

        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Không dùng trong test RoleAssignmentService.");

        public Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectivePermissions.GetValueOrDefault((userId, organizationId)) ?? (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobalPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
    }
}
