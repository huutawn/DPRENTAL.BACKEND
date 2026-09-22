using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class NavigationServiceTests
{
    [Fact]
    public async Task OrganizationContextUsesOnlySelectedMembershipPermissions()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        Guid userId = Guid.NewGuid();
        Guid organizationAId = Guid.NewGuid();
        Guid organizationBId = Guid.NewGuid();
        await SeedUserWithPermissionsAsync(
            dbContext,
            userId,
            organizationAId,
            organizationBId,
            [PermissionCode.IdentityUsersRead],
            [PermissionCode.IdentityMembersManage],
            [PermissionCode.IdentityRolesRead]);

        NavigationService service = CreateService(dbContext);

        Result<Domain.DTOs.Navigation.NavigationResponse> orgA = await service.GetNavigationAsync(userId, organizationAId);
        Result<Domain.DTOs.Navigation.NavigationResponse> orgB = await service.GetNavigationAsync(userId, organizationBId);

        Assert.True(orgA.IsSuccess);
        Assert.Equal(
            [PermissionCode.IdentityMembersManage, PermissionCode.IdentityUsersRead],
            orgA.Value.Permissions);
        Assert.Equal(["identity.members", "identity.users"], ChildKeys(orgA.Value));

        Assert.True(orgB.IsSuccess);
        Assert.Equal(
            [PermissionCode.IdentityRolesRead, PermissionCode.IdentityUsersRead],
            orgB.Value.Permissions);
        Assert.Equal(["identity.roles", "identity.users"], ChildKeys(orgB.Value));
        Assert.DoesNotContain(PermissionCode.IdentityMembersManage, orgB.Value.Permissions);
    }

    [Fact]
    public async Task GlobalPermissionDoesNotBypassOrganizationMembership()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        Guid userId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        await SeedUserWithPermissionsAsync(
            dbContext,
            userId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [PermissionCode.IdentityOrganizationsSuspend],
            [],
            []);
        dbContext.Organizations.Add(new Organization { Id = organizationId, Name = "Other", Slug = "other", Status = OrganizationStatus.Active });
        await dbContext.SaveChangesAsync();

        NavigationService service = CreateService(dbContext);
        Result<Domain.DTOs.Navigation.NavigationResponse> result = await service.GetNavigationAsync(userId, organizationId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task GlobalNavigationDoesNotIncludeScopedPermissions()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        Guid userId = Guid.NewGuid();
        await SeedUserWithPermissionsAsync(
            dbContext,
            userId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [PermissionCode.IdentityUsersRead],
            [PermissionCode.IdentityMembersManage],
            [PermissionCode.IdentityRolesRead]);

        NavigationService service = CreateService(dbContext);
        Result<Domain.DTOs.Navigation.NavigationResponse> result = await service.GetNavigationAsync(userId, null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.OrganizationId);
        Assert.Equal([PermissionCode.IdentityUsersRead], result.Value.Permissions);
        Assert.Equal(["identity.users"], ChildKeys(result.Value));
    }

    [Fact]
    public async Task InactiveMembershipInactiveOrganizationAndEmptyOrganizationIdAreRejected()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        Guid userId = Guid.NewGuid();
        Guid organizationAId = Guid.NewGuid();
        Guid organizationBId = Guid.NewGuid();
        await SeedUserWithPermissionsAsync(
            dbContext,
            userId,
            organizationAId,
            organizationBId,
            [],
            [PermissionCode.IdentityMembersRead],
            []);

        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(item => item.UserId == userId && item.OrganizationId == organizationAId);
        membership.Status = MembershipStatus.Suspended;
        Organization organization = await dbContext.Organizations.SingleAsync(item => item.Id == organizationBId);
        organization.Status = OrganizationStatus.Suspended;
        await dbContext.SaveChangesAsync();

        NavigationService service = CreateService(dbContext);
        Result<Domain.DTOs.Navigation.NavigationResponse> suspended = await service.GetNavigationAsync(userId, organizationAId);
        Result<Domain.DTOs.Navigation.NavigationResponse> inactiveOrganization = await service.GetNavigationAsync(userId, organizationBId);
        Result<Domain.DTOs.Navigation.NavigationResponse> empty = await service.GetNavigationAsync(userId, Guid.Empty);

        Assert.True(suspended.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, suspended.Error);
        Assert.True(inactiveOrganization.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, inactiveOrganization.Error);
        Assert.True(empty.IsFailure);
        Assert.Equal(AuthErrors.Navigation.InvalidOrganizationContext, empty.Error);
    }

    private static string[] ChildKeys(Domain.DTOs.Navigation.NavigationResponse response) =>
        Assert.Single(response.Items).Children.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();

    private static AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static NavigationService CreateService(AuthDbContext dbContext) => new(
        new EfUserRepository(dbContext),
        new EfOrganizationRepository(dbContext),
        new EfOrganizationMembershipRepository(dbContext));

    private static async Task SeedUserWithPermissionsAsync(
        AuthDbContext dbContext,
        Guid userId,
        Guid organizationAId,
        Guid organizationBId,
        IReadOnlyCollection<string> globalPermissions,
        IReadOnlyCollection<string> organizationAPermissions,
        IReadOnlyCollection<string> organizationBPermissions)
    {
        User user = new() { Id = userId, Email = $"{userId:N}@example.com", FullName = "Navigation User", IsActive = true };
        Organization organizationA = new() { Id = organizationAId, Name = "Organization A", Slug = $"org-a-{organizationAId:N}", Status = OrganizationStatus.Active };
        Organization organizationB = new() { Id = organizationBId, Name = "Organization B", Slug = $"org-b-{organizationBId:N}", Status = OrganizationStatus.Active };
        Role globalRole = CreateRole("global", RoleScope.Global, globalPermissions);
        Role orgARole = CreateRole("org-a", RoleScope.Organization, organizationAPermissions);
        Role orgBRole = CreateRole("org-b", RoleScope.Organization, organizationBPermissions);

        dbContext.Users.Add(user);
        dbContext.Organizations.AddRange(organizationA, organizationB);
        dbContext.Roles.AddRange(globalRole, orgARole, orgBRole);
        dbContext.UserRoles.Add(new UserRole { UserId = userId, RoleId = globalRole.Id });
        dbContext.OrganizationMemberships.AddRange(
            new OrganizationMembership { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationAId, RoleId = orgARole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow },
            new OrganizationMembership { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationBId, RoleId = orgBRole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow });

        await dbContext.SaveChangesAsync();
    }

    private static Role CreateRole(string name, RoleScope scope, IReadOnlyCollection<string> permissions)
    {
        Role role = new() { Id = Guid.NewGuid(), Name = $"{name}-{Guid.NewGuid():N}", Scope = scope };
        foreach (string permissionCode in permissions)
        {
            Permission permission = new() { Id = Guid.NewGuid(), Code = permissionCode };
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, Permission = permission });
        }

        return role;
    }
}
