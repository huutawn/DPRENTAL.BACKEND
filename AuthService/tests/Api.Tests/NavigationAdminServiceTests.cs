using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.DTOs.Navigation;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class NavigationAdminServiceTests
{
    [Fact]
    public async Task OnlyGlobalNavigationManagerCanReadOrChangeCatalog()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        NavigationFixture fixture = await SeedAsync(dbContext);
        NavigationAdminService service = CreateService(dbContext);

        Result<IReadOnlyCollection<NavigationAdminItemResponse>> denied = await service.GetTemplateItemsAsync(fixture.NonManagerUserId, OrganizationType.Platform);
        Result<NavigationAdminItemResponse> created = await service.CreateTemplateItemAsync(
            fixture.ManagerUserId,
            OrganizationType.Platform,
            new CreateNavigationItemRequest(
                "identity.audit",
                "Audit",
                "/audit",
                "scroll-text",
                20,
                fixture.RootItemId,
                [fixture.UsersReadPermissionId]));

        Assert.True(denied.IsFailure);
        Assert.Equal(AuthErrors.Navigation.ManageDenied, denied.Error);
        Assert.True(created.IsSuccess, created.Error.Code);
        Assert.Equal("identity.audit", created.Value.Key);
        Assert.Equal([PermissionCode.IdentityUsersRead], created.Value.Permissions.Select(permission => permission.Code));
    }

    [Fact]
    public async Task ItemValidationProtectsHrefTreeAndPermissionInvariants()
    {
        await using AuthDbContext dbContext = CreateDbContext();
        NavigationFixture fixture = await SeedAsync(dbContext);
        NavigationAdminService service = CreateService(dbContext);

        Result<NavigationAdminItemResponse> invalidHref = await service.CreateTemplateItemAsync(
            fixture.ManagerUserId,
            OrganizationType.Platform,
            new CreateNavigationItemRequest(
                "identity.external",
                "External",
                "https://example.com",
                "link",
                20,
                fixture.RootItemId,
                [fixture.UsersReadPermissionId]));
        Result<NavigationAdminItemResponse> created = await service.CreateTemplateItemAsync(
            fixture.ManagerUserId,
            OrganizationType.Platform,
            new CreateNavigationItemRequest(
                "identity.audit",
                "Audit",
                "/audit",
                "scroll-text",
                20,
                fixture.RootItemId,
                [fixture.UsersReadPermissionId]));
        Result removeLast = await service.RemoveTemplatePermissionAsync(
            fixture.ManagerUserId,
            OrganizationType.Platform,
            created.Value.Id,
            fixture.UsersReadPermissionId);
        Result deleteParent = await service.DeleteTemplateItemAsync(fixture.ManagerUserId, OrganizationType.Platform, fixture.RootItemId);

        Assert.True(invalidHref.IsFailure);
        Assert.Equal(AuthErrors.Navigation.InvalidItem, invalidHref.Error);
        Assert.True(created.IsSuccess, created.Error.Code);
        Assert.True(removeLast.IsFailure);
        Assert.Equal(AuthErrors.Navigation.LastPermission, removeLast.Error);
        Assert.True(deleteParent.IsFailure);
        Assert.Equal(AuthErrors.Navigation.ParentHasChildren, deleteParent.Error);
    }

    private static AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static NavigationAdminService CreateService(AuthDbContext dbContext) => new(
        new EfNavigationCatalogRepository(dbContext),
        new EfOrganizationRepository(dbContext),
        new EfRoleRepository(dbContext),
        new EfUserRepository(dbContext));

    private static async Task<NavigationFixture> SeedAsync(AuthDbContext dbContext)
    {
        Guid managerUserId = Guid.NewGuid();
        Guid nonManagerUserId = Guid.NewGuid();
        Permission navigationManage = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityNavigationManage };
        Permission usersRead = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityUsersRead };
        Role managerRole = new() { Id = Guid.NewGuid(), Name = "navigation-admin", Scope = RoleScope.Global };
        managerRole.RolePermissions.Add(new RolePermission { RoleId = managerRole.Id, PermissionId = navigationManage.Id, Permission = navigationManage });

        NavigationCatalog template = new() { Id = Guid.NewGuid(), DashboardType = OrganizationType.Platform, CreatedAtUtc = DateTime.UtcNow };
        NavigationItem root = new() { Id = Guid.NewGuid(), CatalogId = template.Id, Key = "identity", Label = "Identity", Icon = "shield", Order = 10 };
        template.Items.Add(root);

        dbContext.Users.AddRange(
            new User { Id = managerUserId, Email = "navigation-manager@example.com", FullName = "Navigation manager", IsActive = true },
            new User { Id = nonManagerUserId, Email = "navigation-reader@example.com", FullName = "Navigation reader", IsActive = true });
        dbContext.Roles.Add(managerRole);
        dbContext.UserRoles.Add(new UserRole { UserId = managerUserId, RoleId = managerRole.Id });
        dbContext.Permissions.Add(usersRead);
        dbContext.NavigationCatalogs.Add(template);
        await dbContext.SaveChangesAsync();

        return new NavigationFixture(managerUserId, nonManagerUserId, root.Id, usersRead.Id);
    }

    private sealed record NavigationFixture(Guid ManagerUserId, Guid NonManagerUserId, Guid RootItemId, Guid UsersReadPermissionId);
}
