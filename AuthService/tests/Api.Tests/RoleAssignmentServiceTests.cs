using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;

using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class RoleAssignmentServiceTests
{
    [Fact]
    public async Task AssignAsyncRejectsOrganizationScopedRole()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid orgRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[orgRoleId] = new Role { Id = orgRoleId, Name = "OrgOwner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[orgRoleId] = ["perm.one"];
        roleRepo.UserPermissions[actorUserId] = ["perm.one"];

        RoleAssignmentService service = new(roleRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, orgRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InvalidScope, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task RevokeAsyncRejectsOrganizationScopedRole()
    {
        Guid actorUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid orgRoleId = Guid.NewGuid();

        FakeRoleRepository roleRepo = new();
        roleRepo.Roles[orgRoleId] = new Role { Id = orgRoleId, Name = "OrgOwner", Scope = RoleScope.Organization };
        roleRepo.RolePermissions[orgRoleId] = ["perm.one"];
        roleRepo.UserPermissions[actorUserId] = ["perm.one"];

        RoleAssignmentService service = new(roleRepo);

        Result result = await service.RevokeAsync(actorUserId, new AssignRoleRequest(targetUserId, orgRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InvalidScope, result.Error);
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

        RoleAssignmentService service = new(roleRepo);

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

        RoleAssignmentService service = new(roleRepo);

        Result result = await service.AssignAsync(platformAdminUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Role.InsufficientPermission, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.DoesNotContain((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    [Fact]
    public async Task RevokeAsyncConcurrentRemovalReturnsNotAssigned()
    {
        Guid actorId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        FakeRoleRepository roles = new() { RevokeSucceeds = false };
        roles.Roles[roleId] = new Role { Id = roleId, Name = "GlobalAdmin", Scope = RoleScope.Global };
        roles.UserPermissions[actorId] = [PermissionCode.IdentityRolesRevokeGlobal];
        roles.Assigned.Add((targetId, roleId));

        RoleAssignmentService service = new(roles);
        Result result = await service.RevokeAsync(actorId, new AssignRoleRequest(targetId, roleId));

        Assert.Equal(AuthErrors.Role.NotAssigned, result.Error);
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

        RoleAssignmentService service = new(roleRepo);

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

        RoleAssignmentService service = new(roleRepo);

        Result result = await service.RevokeAsync(superAdminUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain((targetUserId, globalRoleId), roleRepo.Assigned);
    }

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public Dictionary<Guid, Role> Roles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> RolePermissions { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> UserPermissions { get; } = [];
        public HashSet<(Guid, Guid)> Assigned { get; } = [];
        public bool RevokeSucceeds { get; set; } = true;

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
            if (RevokeSucceeds)
            {
                Assigned.Remove((userId, roleId));
            }
            return Task.FromResult(RevokeSucceeds);
        }
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
