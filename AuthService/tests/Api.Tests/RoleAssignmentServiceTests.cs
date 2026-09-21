using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.IRepositories;
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
        Assert.Equal(RoleErrors.InvalidScope, result.Error);
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
        Assert.Equal(RoleErrors.InvalidScope, result.Error);
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
        roleRepo.UserPermissions[actorUserId] = ["perm.one"];

        RoleAssignmentService service = new(roleRepo);

        Result result = await service.AssignAsync(actorUserId, new AssignRoleRequest(targetUserId, globalRoleId));

        Assert.True(result.IsSuccess);
        Assert.Contains((targetUserId, globalRoleId), roleRepo.Assigned);
    }

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
}
