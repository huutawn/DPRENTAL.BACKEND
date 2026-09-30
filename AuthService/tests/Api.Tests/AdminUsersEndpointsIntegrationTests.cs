using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AuthService.Application.DTOs.Auth;
using AuthService.Application.DTOs.User;
using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class AdminUsersEndpointsIntegrationTests
{
    // =========================================================================
    // 1. GET /admin/users
    // =========================================================================

    [Fact]
    public async Task GetUsers_UnauthorizedAndForbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        // 1. Unauthenticated -> 401
        using HttpClient anonClient = factory.CreateClient();
        using HttpResponseMessage unauthRes = await anonClient.GetAsync("/api/v1/auth/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 2. Authenticated user without Users.Read permission -> 403
        Guid normalUserId = Guid.NewGuid();
        await SeedUserAsync(factory, normalUserId, "normal@example.com", "Normal User", true);
        using HttpClient normalClient = CreateClientWithToken(factory, normalUserId, ["Customer"], []);
        using HttpResponseMessage forbidRes = await normalClient.GetAsync("/api/v1/auth/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidRes.StatusCode);
    }

    [Fact]
    public async Task GetUsers_PagingAndFiltering_ReturnsActiveAndLockedUsersWithRoles()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid roleAdminId = Guid.NewGuid();
        Guid roleCustomerId = Guid.NewGuid();
        await SeedRoleAsync(factory, roleAdminId, "Admin", RoleScope.Global);
        await SeedRoleAsync(factory, roleCustomerId, "Customer", RoleScope.Global);

        Guid userActiveId = Guid.NewGuid();
        Guid userLockedId = Guid.NewGuid();
        await SeedUserAsync(factory, userActiveId, "active.cus@example.com", "Active Customer", true, [roleCustomerId]);
        await SeedUserAsync(factory, userLockedId, "locked.cus@example.com", "Locked Customer", false, [roleCustomerId]);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Read, Permissions.Users.Manage]);

        // Filter by role "Customer" - should return both active and locked customer
        using HttpResponseMessage response = await adminClient.GetAsync("/api/v1/auth/admin/users?Type=Customer&Page=1&PageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PagedResult<AdminUserListItemDto>? paged = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItemDto>>();
        Assert.NotNull(paged);
        Assert.True(paged.TotalCount >= 2);

        var activeItem = paged.Items.FirstOrDefault(u => u.Id == userActiveId);
        var lockedItem = paged.Items.FirstOrDefault(u => u.Id == userLockedId);

        Assert.NotNull(activeItem);
        Assert.True(activeItem.IsActive);
        Assert.Contains("Customer", activeItem.Roles);

        Assert.NotNull(lockedItem);
        Assert.False(lockedItem.IsActive); // Locked user is included!
        Assert.Contains("Customer", lockedItem.Roles);
    }

    // =========================================================================
    // 2. GET /admin/users/{id}
    // =========================================================================

    [Fact]
    public async Task GetUserById_ReturnsLockedUserDetail()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid roleId = Guid.NewGuid();
        await SeedRoleAsync(factory, roleId, "Customer", RoleScope.Global);

        Guid lockedUserId = Guid.NewGuid();
        await SeedUserAsync(factory, lockedUserId, "locked@example.com", "Locked Guy", false, [roleId]);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Read]);

        using HttpResponseMessage response = await adminClient.GetAsync($"/api/v1/auth/admin/users/{lockedUserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AdminUserDetailDto? detail = await response.Content.ReadFromJsonAsync<AdminUserDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(lockedUserId, detail.Id);
        Assert.False(detail.IsActive); // Allows viewing detail of locked user!
        Assert.Equal("locked@example.com", detail.Email);
        Assert.Equal("Locked Guy", detail.FullName);
    }

    [Fact]
    public async Task GetUserById_NotFound_Returns404()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Read]);

        using HttpResponseMessage response = await adminClient.GetAsync($"/api/v1/auth/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =========================================================================
    // 3. POST /admin/users/{id}/suspend
    // =========================================================================

    [Fact]
    public async Task SuspendUser_SoleOwner_Returns409Conflict()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, ownerId, "sole.owner@example.com", "Sole Owner", true);

        Guid orgId = Guid.NewGuid();
        await SeedOrganizationWithOwnerAsync(factory, orgId, ownerId);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage]);

        using HttpResponseMessage response = await adminClient.PostAsync($"/api/v1/auth/admin/users/{ownerId}/suspend", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Verify user remains active
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User? owner = await db.Users.FindAsync([ownerId]);
        Assert.NotNull(owner);
        Assert.True(owner.IsActive);
    }

    [Fact]
    public async Task SuspendUser_Success_DeactivatesAndRevokesSessions()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid targetId = Guid.NewGuid();
        await SeedUserAsync(factory, targetId, "target@example.com", "Target User", true);

        Guid targetSession = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = targetSession,
                UserId = targetId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage]);

        using HttpResponseMessage response = await adminClient.PostAsync($"/api/v1/auth/admin/users/{targetId}/suspend", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify deactivated and session revoked
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            User? target = await db.Users.FindAsync([targetId]);
            Assert.NotNull(target);
            Assert.False(target.IsActive);

            Session? s = await db.Sessions.FindAsync([targetSession]);
            Assert.NotNull(s);
            Assert.NotNull(s.RevokedAtUtc);
        }
    }

    // =========================================================================
    // 4. POST /admin/users/{id}/activate
    // =========================================================================

    [Fact]
    public async Task ActivateUser_Success_ReactivatesUser()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid targetId = Guid.NewGuid();
        await SeedUserAsync(factory, targetId, "target@example.com", "Target User", false);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage]);

        using HttpResponseMessage response = await adminClient.PostAsync($"/api/v1/auth/admin/users/{targetId}/activate", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User? target = await db.Users.FindAsync([targetId]);
        Assert.NotNull(target);
        Assert.True(target.IsActive);
    }

    // =========================================================================
    // 5. POST /admin/users
    // =========================================================================

    [Fact]
    public async Task CreateUser_NonGlobalRole_ReturnsInvalidScope()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid orgRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, orgRoleId, "OrgRole", RoleScope.Organization);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal]);

        CreateUserRequest req = new()
        {
            Email = "newuser@example.com",
            Password = "Password123!",
            FullName = "New User",
            RoleIds = [orgRoleId]
        };
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync("/api/v1/auth/admin/users", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_RoleWithUnownedPermissions_ReturnsInsufficientPermission()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        Guid adminRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, adminRoleId, "Admin", RoleScope.Global, [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal]);
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true, [adminRoleId]);

        Guid superRoleId = Guid.NewGuid();
        // SuperRole has high-privilege permission that admin doesn't have
        await SeedRoleAsync(factory, superRoleId, "SuperRole", RoleScope.Global, [PermissionCode.IdentityRolesRevokeGlobal]);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal]);

        CreateUserRequest req = new()
        {
            Email = "newuser@example.com",
            Password = "Password123!",
            FullName = "New User",
            RoleIds = [superRoleId]
        };
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync("/api/v1/auth/admin/users", req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_Success_ReturnsNewUserId()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        Guid adminRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, adminRoleId, "Admin", RoleScope.Global,
            [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true, [adminRoleId]);

        Guid roleId = Guid.NewGuid();
        await SeedRoleAsync(factory, roleId, "CustomerRole", RoleScope.Global, [PermissionCode.IdentityOrganizationsRead]);
        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"],
            [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);

        CreateUserRequest req = new()
        {
            Email = "created@example.com",
            Password = "Password123!",
            FullName = "Created User",
            RoleIds = [roleId]
        };
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync("/api/v1/auth/admin/users", req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Guid newUserId = await response.Content.ReadFromJsonAsync<Guid>();
        Assert.NotEqual(Guid.Empty, newUserId);
    }

    // =========================================================================
    // 6. PUT /admin/users/{id}
    // =========================================================================

    [Fact]
    public async Task UpdateUser_SelfRoleChange_ReturnsCannotSelfAssign()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        await SeedRoleAsync(factory, roleId, "Admin", RoleScope.Global);
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true, [roleId]);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal]);

        UpdateUserRequest req = new()
        {
            FullName = "Admin Renamed",
            RoleIds = [roleId]
        };
        // Admin attempts to update their own user with RoleIds
        using HttpResponseMessage response = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/users/{adminId}", req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AuthErrors.Role.CannotSelfAssign.Code, doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UpdateUser_DeactivateSoleOwner_Returns409Conflict()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true);

        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner User", true);
        Guid orgId = Guid.NewGuid();
        await SeedOrganizationWithOwnerAsync(factory, orgId, ownerId);

        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"], [Permissions.Users.Manage]);

        UpdateUserRequest req = new()
        {
            FullName = "Owner User",
            IsActive = false
        };
        using HttpResponseMessage response = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/users/{ownerId}", req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_RoleChange_RevokesUserSessionsImmediately()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();
        Guid adminRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, adminRoleId, "Admin", RoleScope.Global,
            [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true, [adminRoleId]);

        Guid oldRoleId = Guid.NewGuid();
        Guid newRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, oldRoleId, "OldRole", RoleScope.Global, [PermissionCode.IdentityOrganizationsRead]);
        await SeedRoleAsync(factory, newRoleId, "NewRole", RoleScope.Global, [PermissionCode.IdentityOrganizationsRead]);

        Guid targetId = Guid.NewGuid();
        await SeedUserAsync(factory, targetId, "target@example.com", "Target User", true, [oldRoleId]);

        Guid targetSessionId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = targetSessionId,
                UserId = targetId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        // Issue token for target user before role change
        using HttpClient targetClient = CreateClientWithToken(factory, targetId, ["OldRole"], [], targetSessionId);

        // Before role change, target token can access users/me
        using HttpResponseMessage beforeRes = await targetClient.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.OK, beforeRes.StatusCode);

        // Admin changes target user's role to newRoleId
        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"],
            [Permissions.Users.Manage, PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);

        UpdateUserRequest updateReq = new()
        {
            FullName = "Target User",
            RoleIds = [newRoleId]
        };
        using HttpResponseMessage updateRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/users/{targetId}", updateReq);
        Assert.Equal(HttpStatusCode.NoContent, updateRes.StatusCode);

        // Session in DB must be revoked now
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Session? s = await db.Sessions.FindAsync([targetSessionId]);
            Assert.NotNull(s);
            Assert.NotNull(s.RevokedAtUtc);
        }

        // After role change, target's token is immediately rejected with 401 Unauthorized!
        using HttpResponseMessage afterRes = await targetClient.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRes.StatusCode);
    }

    // =========================================================================
    // 7. GET /admin/users/roles
    // =========================================================================

    [Fact]
    public async Task GetRoles_NonAdmin_ReturnsForbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid normalUserId = Guid.NewGuid();
        await SeedUserAsync(factory, normalUserId, "normal@example.com", "Normal User", true);

        // Token has role "Customer" instead of "Admin" ("adm")
        using HttpClient client = CreateClientWithToken(factory, normalUserId, ["Customer"], []);
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/admin/users/roles");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_OnlyDisplaysAssignableGlobalRoles()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid adminId = Guid.NewGuid();

        Guid assignableRoleId = Guid.NewGuid();
        Guid unownedPermRoleId = Guid.NewGuid();
        Guid orgScopedRoleId = Guid.NewGuid();

        // 1. Role 1: Global, with IdentityOrganizationsRead
        await SeedRoleAsync(factory, assignableRoleId, "AssignableRole", RoleScope.Global, [PermissionCode.IdentityOrganizationsRead]);

        // 2. Role 2: Global, with IdentityRolesRevokeGlobal (which admin will NOT have)
        await SeedRoleAsync(factory, unownedPermRoleId, "UnownedPermRole", RoleScope.Global, [PermissionCode.IdentityRolesRevokeGlobal]);

        // 3. Role 3: Organization scope (should never be returned)
        await SeedRoleAsync(factory, orgScopedRoleId, "OrgScopedRole", RoleScope.Organization);

        Guid adminRoleId = Guid.NewGuid();
        await SeedRoleAsync(factory, adminRoleId, "Admin", RoleScope.Global,
            [PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin User", true, [adminRoleId]);
        using HttpClient adminClient = CreateClientWithToken(
            factory, adminId, ["Admin"],
            [PermissionCode.IdentityRolesAssignGlobal, PermissionCode.IdentityOrganizationsRead]);

        using HttpResponseMessage response = await adminClient.GetAsync("/api/v1/auth/admin/users/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<RoleDto>? roles = await response.Content.ReadFromJsonAsync<List<RoleDto>>();
        Assert.NotNull(roles);

        // Must contain AssignableRole
        Assert.Contains(roles, r => r.Id == assignableRoleId && r.Name == "AssignableRole");

        // Must NOT contain UnownedPermRole (contains permission admin doesn't have)
        Assert.DoesNotContain(roles, r => r.Id == unownedPermRoleId);

        // Must NOT contain OrgScopedRole (scope != Global)
        Assert.DoesNotContain(roles, r => r.Id == orgScopedRoleId);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static HttpClient CreateClientWithToken(
        WebApplicationFactory<Program> factory,
        Guid userId,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions,
        Guid? sessionId = null)
    {
        HttpClient client = factory.CreateClient();
        Guid finalSessionId = sessionId ?? Guid.NewGuid();

        if (!sessionId.HasValue)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = finalSessionId,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            db.SaveChanges();
        }

        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            userId,
            Roles: roles,
            Permissions: permissions,
            SessionId: finalSessionId));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string email,
        string fullName,
        bool isActive,
        IEnumerable<Guid>? roleIds = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = email,
            FullName = fullName,
            CreatedAt = DateTime.UtcNow,
            IsActive = isActive,
            HasPassword = true,
            ProfileCompletedAtUtc = DateTime.UtcNow
        };

        if (roleIds is not null)
        {
            foreach (Guid roleId in roleIds)
            {
                user.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
            }
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static async Task SeedRoleAsync(
        WebApplicationFactory<Program> factory,
        Guid roleId,
        string name,
        RoleScope scope,
        IEnumerable<string>? permissionCodes = null)
    {
        using IServiceScope s = factory.Services.CreateScope();
        AuthDbContext db = s.ServiceProvider.GetRequiredService<AuthDbContext>();

        Role role = new()
        {
            Id = roleId,
            Name = name,
            Description = $"{name} description",
            Scope = scope
        };

        if (permissionCodes is not null)
        {
            foreach (string permCode in permissionCodes)
            {
                Permission? perm = await db.Permissions.FirstOrDefaultAsync(p => p.Code == permCode);
                if (perm is null)
                {
                    perm = new Permission
                    {
                        Id = Guid.NewGuid(),
                        Code = permCode,
                        Description = permCode
                    };
                    db.Permissions.Add(perm);
                }

                role.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = perm.Id,
                    Permission = perm
                });
            }
        }

        db.Roles.Add(role);
        await db.SaveChangesAsync();
    }

    private static async Task SeedOrganizationWithOwnerAsync(
        WebApplicationFactory<Program> factory,
        Guid organizationId,
        Guid ownerUserId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Organization org = new()
        {
            Id = organizationId,
            Name = "Admin Test Org",
            Slug = $"org-{organizationId:N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };

        Guid ownerRoleId = new("b1010000-0000-0000-0000-000000000001");
        Role? role = await dbContext.Roles.FindAsync([ownerRoleId]);
        if (role is null)
        {
            role = new Role { Id = ownerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };
            dbContext.Roles.Add(role);
        }

        OrganizationMembership membership = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            UserId = ownerUserId,
            RoleId = ownerRoleId,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        };

        dbContext.Organizations.Add(org);
        dbContext.OrganizationMemberships.Add(membership);
        await dbContext.SaveChangesAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        string publicKey = rsa.ExportRSAPublicKeyPem();
        string privateKey = rsa.ExportRSAPrivateKeyPem();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MediaService:ApiKey", "test-media-key");
            builder.UseSetting("MediaService:PublicGatewayUrl", "http://gateway.example.test");
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Jwt:Issuer"] = "https://auth.example.test",
                    ["Auth:Jwt:Audience"] = "dprental-clients",
                    ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
                    ["Auth:Jwt:ActiveKeyId"] = "current",
                    ["Auth:Jwt:Keys:0:KeyId"] = "current",
                    ["Auth:Jwt:Keys:0:PublicKeyPem"] = publicKey,
                    ["Auth:Jwt:Keys:0:PrivateKeyPem"] = privateKey,
                    ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
            });
        });
    }
}
