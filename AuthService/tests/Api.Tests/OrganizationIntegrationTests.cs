using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class OrganizationIntegrationTests
{
    private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
    private static readonly Guid CustomerRoleId = new("82127c20-a094-4d8b-a1c7-b94266c001d2");
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

    [Fact]
    public async Task CompleteOrganizationLifecycleEndToEnd()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid userAId = Guid.NewGuid();
        Guid userBId = Guid.NewGuid();
        await CreateUserAsync(factory, userAId, "usera@example.com", "User A", AdminRoleId);
        await CreateUserAsync(factory, userBId, "userb@example.com", "User B", CustomerRoleId);

        using HttpClient clientA = CreateAuthenticatedClient(factory, userAId, "admin");
        using HttpClient clientB = CreateAuthenticatedClient(factory, userBId, "customer");

        // 1. User A creates organization
        CreateOrganizationRequest createReq = new("Rent Express", "rent-express", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await clientA.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);
        Guid orgId = org.Id;

        // 2. User A invites User B
        AddOrganizationMemberRequest addReq = new(userBId, MemberRoleId);
        using HttpResponseMessage addRes = await clientA.PostAsJsonAsync($"/api/v1/auth/organizations/{orgId}/members", addReq);
        Assert.Equal(HttpStatusCode.Created, addRes.StatusCode);
        OrganizationMemberResponse? invitedMember = await addRes.Content.ReadFromJsonAsync<OrganizationMemberResponse>();
        Assert.NotNull(invitedMember);
        Assert.Equal(MembershipStatus.Invited, invitedMember.Status);

        // 3. User B checks /users/me - organization is NOT reflected because status is Invited
        using HttpResponseMessage meBeforeAccept = await clientB.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.OK, meBeforeAccept.StatusCode);
        using JsonDocument docBefore = JsonDocument.Parse(await meBeforeAccept.Content.ReadAsStringAsync());
        Assert.Empty(docBefore.RootElement.GetProperty("organizations").EnumerateArray());

        // 4. User B self-accepts invitation
        using HttpResponseMessage acceptRes = await clientB.PostAsync($"/api/v1/auth/organizations/{orgId}/members/accept", null);
        Assert.Equal(HttpStatusCode.NoContent, acceptRes.StatusCode);

        // 5. User A assigns Organization Owner role to User B
        PatchOrganizationMemberRequest patchRoleReq = new(RoleId: OwnerRoleId);
        using HttpResponseMessage patchRoleRes = await clientA.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}/members/{userBId}", patchRoleReq);
        Assert.Equal(HttpStatusCode.NoContent, patchRoleRes.StatusCode);

        // 6. User B checks /users/me - now reflects organization and Owner role!
        using HttpResponseMessage meAfterRole = await clientB.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.OK, meAfterRole.StatusCode);
        using JsonDocument docAfter = JsonDocument.Parse(await meAfterRole.Content.ReadAsStringAsync());
        JsonElement orgElement = Assert.Single(docAfter.RootElement.GetProperty("organizations").EnumerateArray());
        Assert.Equal(orgId, orgElement.GetProperty("organizationId").GetGuid());
        Assert.Equal("rent-express", orgElement.GetProperty("slug").GetString());
        Assert.Equal("Owner", orgElement.GetProperty("roleName").GetString());

        // 7. User B can access organization API
        using HttpResponseMessage getOrgRes = await clientB.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, getOrgRes.StatusCode);

        // 8. User A suspends User B
        PatchOrganizationMemberRequest suspendReq = new(Status: MembershipStatus.Suspended);
        using HttpResponseMessage suspendRes = await clientA.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}/members/{userBId}", suspendReq);
        Assert.Equal(HttpStatusCode.NoContent, suspendRes.StatusCode);

        // 9. /users/me no longer grants rights, and organization API returns 403
        using HttpResponseMessage meAfterSuspend = await clientB.GetAsync("/api/v1/auth/users/me");
        using JsonDocument docSuspend = JsonDocument.Parse(await meAfterSuspend.Content.ReadAsStringAsync());
        Assert.Empty(docSuspend.RootElement.GetProperty("organizations").EnumerateArray());

        using HttpResponseMessage getOrgAfterSuspend = await clientB.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, getOrgAfterSuspend.StatusCode);

        // 10. User A reactivates and then removes User B
        PatchOrganizationMemberRequest reactivateReq = new(Status: MembershipStatus.Active);
        using HttpResponseMessage reactivateRes = await clientA.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}/members/{userBId}", reactivateReq);
        Assert.Equal(HttpStatusCode.NoContent, reactivateRes.StatusCode);

        using HttpResponseMessage deleteRes = await clientA.DeleteAsync($"/api/v1/auth/organizations/{orgId}/members/{userBId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // User B API access is gone
        using HttpResponseMessage getOrgAfterDelete = await clientB.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, getOrgAfterDelete.StatusCode);
    }

    [Fact]
    public async Task MemberListVisibilityReturnsExpectedProperties()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin User", AdminRoleId);

        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        CreateOrganizationRequest createReq = new("List Corp", "list-corp", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await client.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        using HttpResponseMessage listRes = await client.GetAsync($"/api/v1/auth/organizations/{org.Id}/members");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        IReadOnlyList<OrganizationMemberResponse>? members = await listRes.Content.ReadFromJsonAsync<IReadOnlyList<OrganizationMemberResponse>>();
        Assert.NotNull(members);
        OrganizationMemberResponse creator = Assert.Single(members);

        Assert.Equal(adminId, creator.UserId);
        Assert.Equal("admin@example.com", creator.Email);
        Assert.Equal("Admin User", creator.FullName);
        Assert.Equal(OwnerRoleId, creator.RoleId);
        Assert.Equal("Owner", creator.RoleName);
        Assert.Equal(MembershipStatus.Active, creator.Status);
        Assert.True(creator.JoinedAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public async Task NonMemberAndCrossOrganizationReturnsForbidden403()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();
        await CreateUserAsync(factory, user1, "u1@example.com", "User 1", AdminRoleId);
        await CreateUserAsync(factory, user2, "u2@example.com", "User 2", CustomerRoleId);

        using HttpClient client1 = CreateAuthenticatedClient(factory, user1, "admin");
        using HttpClient client2 = CreateAuthenticatedClient(factory, user2, "customer");

        CreateOrganizationRequest createReq = new("Org 1", "org-1", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await client1.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        // User 2 (not member) accesses Org 1 -> 403
        using HttpResponseMessage getRes = await client2.GetAsync($"/api/v1/auth/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, getRes.StatusCode);

        using HttpResponseMessage listMembersRes = await client2.GetAsync($"/api/v1/auth/organizations/{org.Id}/members");
        Assert.Equal(HttpStatusCode.Forbidden, listMembersRes.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminCanDeactivateInspectAndReactivateOrganization()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid normalUserId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, normalUserId, "normal@example.com", "Normal", CustomerRoleId);

        using HttpClient adminClient = CreateAuthenticatedClient(factory, adminId, "admin");
        using HttpClient normalClient = CreateAuthenticatedClient(factory, normalUserId, "customer");

        CreateOrganizationRequest createReq = new("Deact Corp", "deact-corp", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await adminClient.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        // Normal user attempts to change isActive -> 403
        UpdateOrganizationRequest normalUpdate = new(IsActive: false);
        using HttpResponseMessage normalUpdateRes = await normalClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}", normalUpdate);
        Assert.Equal(HttpStatusCode.Forbidden, normalUpdateRes.StatusCode);

        // Platform admin deactivates organization -> 204
        UpdateOrganizationRequest adminDeact = new(IsActive: false);
        using HttpResponseMessage adminDeactRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}", adminDeact);
        Assert.Equal(HttpStatusCode.NoContent, adminDeactRes.StatusCode);

        // Normal user accesses deactivated organization -> 403
        using HttpResponseMessage normalGet = await normalClient.GetAsync($"/api/v1/auth/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, normalGet.StatusCode);

        // Platform admin can inspect deactivated organization -> 200
        using HttpResponseMessage adminGet = await adminClient.GetAsync($"/api/v1/auth/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);

        // Platform admin reactivates organization -> 204
        UpdateOrganizationRequest adminReact = new(IsActive: true);
        using HttpResponseMessage adminReactRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}", adminReact);
        Assert.Equal(HttpStatusCode.NoContent, adminReactRes.StatusCode);
    }

    [Fact]
    public async Task DuplicateSlugAndDuplicateMembershipReturnConflict409()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, targetUserId, "target@example.com", "Target", CustomerRoleId);

        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        CreateOrganizationRequest create1 = new("Conflict Corp", "conflict-corp", OrganizationType.Supplier);
        using HttpResponseMessage res1 = await client.PostAsJsonAsync("/api/v1/auth/organizations", create1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        OrganizationResponse? org = await res1.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        // Duplicate slug -> 409
        CreateOrganizationRequest create2 = new("Conflict Corp 2", "conflict-corp", OrganizationType.Renter);
        using HttpResponseMessage res2 = await client.PostAsJsonAsync("/api/v1/auth/organizations", create2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

        // Add member once -> 201
        AddOrganizationMemberRequest addMem = new(targetUserId, MemberRoleId);
        using HttpResponseMessage addMemRes1 = await client.PostAsJsonAsync($"/api/v1/auth/organizations/{org.Id}/members", addMem);
        Assert.Equal(HttpStatusCode.Created, addMemRes1.StatusCode);

        // Duplicate member -> 409
        using HttpResponseMessage addMemRes2 = await client.PostAsJsonAsync($"/api/v1/auth/organizations/{org.Id}/members", addMem);
        Assert.Equal(HttpStatusCode.Conflict, addMemRes2.StatusCode);
    }

    [Fact]
    public async Task MissingIdsReturnNotFound404()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        Guid randomOrgId = Guid.NewGuid();

        // Missing org GET -> 404
        using HttpResponseMessage getRes = await client.GetAsync($"/api/v1/auth/organizations/{randomOrgId}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // Missing org member list -> 404
        using HttpResponseMessage getMembersRes = await client.GetAsync($"/api/v1/auth/organizations/{randomOrgId}/members");
        Assert.Equal(HttpStatusCode.NotFound, getMembersRes.StatusCode);

        // Missing user on add member -> 404
        CreateOrganizationRequest createReq = new("Valid Org", "valid-org", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await client.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        AddOrganizationMemberRequest missingUserReq = new(Guid.NewGuid(), MemberRoleId);
        using HttpResponseMessage addMissingUserRes = await client.PostAsJsonAsync($"/api/v1/auth/organizations/{org.Id}/members", missingUserReq);
        Assert.Equal(HttpStatusCode.NotFound, addMissingUserRes.StatusCode);
    }

    [Fact]
    public async Task InvalidPayloadsReturnBadRequest400()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        // Invalid slug format -> 400
        CreateOrganizationRequest invalidSlug = new("Bad Slug", "invalid slug format!", OrganizationType.Supplier);
        using HttpResponseMessage invalidSlugRes = await client.PostAsJsonAsync("/api/v1/auth/organizations", invalidSlug);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSlugRes.StatusCode);

        CreateOrganizationRequest validCreate = new("Good Org", "good-org", OrganizationType.Supplier);
        using HttpResponseMessage validRes = await client.PostAsJsonAsync("/api/v1/auth/organizations", validCreate);
        OrganizationResponse? org = await validRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        // Empty PATCH -> 400
        using HttpResponseMessage emptyPatchRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, emptyPatchRes.StatusCode);

        // No-op PATCH -> 400
        UpdateOrganizationRequest noopPatch = new(Name: "Good Org", IsActive: true);
        using HttpResponseMessage noopRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}", noopPatch);
        Assert.Equal(HttpStatusCode.BadRequest, noopRes.StatusCode);
    }

    [Fact]
    public async Task CannotRemoveOrSuspendLastOwnerReturnsBadRequest400()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        CreateOrganizationRequest createReq = new("Solo Owner Org", "solo-owner", OrganizationType.Supplier);
        using HttpResponseMessage createRes = await client.PostAsJsonAsync("/api/v1/auth/organizations", createReq);
        OrganizationResponse? org = await createRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);

        // Attempt to suspend sole owner -> 400
        PatchOrganizationMemberRequest suspendReq = new(Status: MembershipStatus.Suspended);
        using HttpResponseMessage suspendRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{org.Id}/members/{adminId}", suspendReq);
        Assert.Equal(HttpStatusCode.BadRequest, suspendRes.StatusCode);

        // Attempt to remove sole owner -> 400
        using HttpResponseMessage deleteRes = await client.DeleteAsync($"/api/v1/auth/organizations/{org.Id}/members/{adminId}");
        Assert.Equal(HttpStatusCode.BadRequest, deleteRes.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        Dictionary<string, string?> settings = new()
        {
            ["Auth:Jwt:Issuer"] = "https://auth.example.test",
            ["Auth:Jwt:Audience"] = "dprental-clients",
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Auth:Jwt:ActiveKeyId"] = "current",
            ["Auth:Jwt:Keys:0:KeyId"] = "current",
            ["Auth:Jwt:Keys:0:PublicKeyPem"] = rsa.ExportRSAPublicKeyPem(),
            ["Auth:Jwt:Keys:0:PrivateKeyPem"] = rsa.ExportRSAPrivateKeyPem()
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
            });
        });
    }

    private static async Task SeedDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Role adminRole = new() { Id = AdminRoleId, Name = "admin", Scope = RoleScope.Global };
        Role customerRole = new() { Id = CustomerRoleId, Name = "customer", Scope = RoleScope.Global };
        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        Role memberRole = new() { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };

        Permission pCreate = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Create };
        Permission pRead = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Read };
        Permission pUpdate = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Update };
        Permission pMemRead = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Members.Read };
        Permission pMemManage = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Members.Manage };
        Permission pPlatform = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Suspend };
        Permission pAssign = new() { Id = Guid.NewGuid(), Code = Permissions.Roles.Assign };
        Permission pRevoke = new() { Id = Guid.NewGuid(), Code = Permissions.Roles.Revoke };

        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pCreate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pUpdate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pMemRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pMemManage });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pPlatform });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pAssign });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRevoke });

        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pRead });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pUpdate });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pMemRead });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pMemManage });

        memberRole.RolePermissions.Add(new RolePermission { Role = memberRole, Permission = pRead });
        memberRole.RolePermissions.Add(new RolePermission { Role = memberRole, Permission = pMemRead });

        db.Permissions.AddRange(pCreate, pRead, pUpdate, pMemRead, pMemManage, pPlatform, pAssign, pRevoke);
        db.Roles.AddRange(adminRole, customerRole, ownerRole, memberRole);
        await db.SaveChangesAsync();
    }

    private static async Task CreateUserAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string email,
        string fullName,
        Guid roleId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = email,
            FullName = fullName,
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        user.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> factory,
        Guid userId,
        params string[] roles)
    {
        HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, roles));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }
}
