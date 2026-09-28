using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;

using Dprental.AuthService.Application.DTOs.Organization;
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
    public async Task RegisterApproveLifecycleEndToEnd()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid outsiderId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        await CreateUserAsync(factory, outsiderId, "outsider@example.com", "Outsider", CustomerRoleId);

        using HttpClient adminClient = CreateAuthenticatedClient(factory, adminId, "admin");
        using HttpClient ownerClient = CreateAuthenticatedClient(factory, ownerId, "customer");
        using HttpClient outsiderClient = CreateAuthenticatedClient(factory, outsiderId, "customer");

        // 1. Người dùng tự đăng ký tổ chức -> Pending, họ là Owner
        using HttpResponseMessage registerRes = await ownerClient.PostAsJsonAsync("/api/v1/auth/organizations/register", Profile("rent-express"));
        Assert.Equal(HttpStatusCode.Created, registerRes.StatusCode);
        OrganizationRegisteredResponse? registered = await registerRes.Content.ReadFromJsonAsync<OrganizationRegisteredResponse>();
        Assert.NotNull(registered);
        Assert.Equal(OrganizationStatus.Pending, registered.Status);
        Guid orgId = registered.OrganizationId;

        // 2. Owner xem được hồ sơ đang chờ duyệt, người ngoài thì không
        using HttpResponseMessage ownerGet = await ownerClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
        using HttpResponseMessage outsiderGet = await outsiderClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, outsiderGet.StatusCode);

        // 3. Admin duyệt bằng PATCH
        using HttpResponseMessage approveRes = await adminClient.PatchAsync($"/api/v1/auth/organizations/{orgId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approveRes.StatusCode);
        OrganizationResponse? approved = await approveRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(approved);
        Assert.Equal(OrganizationStatus.Active, approved.Status);

        // 4. Owner thấy tổ chức trong /users/me với vai trò Owner
        using HttpResponseMessage meRes = await ownerClient.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        using JsonDocument meDoc = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync());
        JsonElement orgElement = Assert.Single(meDoc.RootElement.GetProperty("organizations").EnumerateArray());
        Assert.Equal(orgId, orgElement.GetProperty("organizationId").GetGuid());
        Assert.Equal("Owner", orgElement.GetProperty("roleName").GetString());

        // 5. MVP không có API thành viên
        using HttpResponseMessage membersRes = await ownerClient.GetAsync($"/api/v1/auth/organizations/{orgId}/members");
        Assert.Equal(HttpStatusCode.NotFound, membersRes.StatusCode);
    }

    [Fact]
    public async Task AdminCannotReviewOwnOrganizationButAnotherAdminCan()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminOwnerId = Guid.NewGuid();
        Guid otherAdminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminOwnerId, "admin-owner@example.com", "Admin Owner", AdminRoleId);
        await CreateUserAsync(factory, otherAdminId, "other-admin@example.com", "Other Admin", AdminRoleId);

        using HttpClient adminOwnerClient = CreateAuthenticatedClient(factory, adminOwnerId, "admin");
        using HttpClient otherAdminClient = CreateAuthenticatedClient(factory, otherAdminId, "admin");

        using HttpResponseMessage registerRes = await adminOwnerClient.PostAsJsonAsync("/api/v1/auth/organizations/register", Profile("platform-rental"));
        OrganizationRegisteredResponse? registered = await registerRes.Content.ReadFromJsonAsync<OrganizationRegisteredResponse>();
        Assert.NotNull(registered);

        using HttpResponseMessage selfApprove = await adminOwnerClient.PatchAsync($"/api/v1/auth/organizations/{registered.OrganizationId}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        using HttpResponseMessage selfReject = await adminOwnerClient.PatchAsJsonAsync(
            $"/api/v1/auth/organizations/{registered.OrganizationId}/reject", new RejectOrganizationRequest("Tự từ chối"));
        Assert.Equal(HttpStatusCode.Forbidden, selfReject.StatusCode);

        using HttpResponseMessage otherApprove = await otherAdminClient.PatchAsync($"/api/v1/auth/organizations/{registered.OrganizationId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, otherApprove.StatusCode);
    }

    [Fact]
    public async Task CreateOrganizationEndpointIsRemoved()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        // Route /organizations chỉ còn GET (danh sách); mọi tổ chức phải đi qua /register rồi được duyệt.
        using HttpResponseMessage res = await client.PostAsJsonAsync("/api/v1/auth/organizations", new { name = "Direct", slug = "direct", type = 1 });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task NonMemberAndCrossOrganizationReturnsForbidden403()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid ownerId = Guid.NewGuid();
        Guid outsiderId = Guid.NewGuid();
        await CreateUserAsync(factory, ownerId, "u1@example.com", "User 1", CustomerRoleId);
        await CreateUserAsync(factory, outsiderId, "u2@example.com", "User 2", CustomerRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, ownerId, "org-1", OrganizationStatus.Active);

        using HttpClient outsiderClient = CreateAuthenticatedClient(factory, outsiderId, "customer");

        using HttpResponseMessage getRes = await outsiderClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, getRes.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminCanDeactivateInspectAndReactivateOrganization()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid normalUserId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, normalUserId, "normal@example.com", "Normal", CustomerRoleId);
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, ownerId, "deact-corp", OrganizationStatus.Active);

        using HttpClient adminClient = CreateAuthenticatedClient(factory, adminId, "admin");
        using HttpClient normalClient = CreateAuthenticatedClient(factory, normalUserId, "customer");

        // Normal user attempts to change isActive -> 403
        using HttpResponseMessage normalUpdateRes = await normalClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new UpdateOrganizationRequest(IsActive: false));
        Assert.Equal(HttpStatusCode.Forbidden, normalUpdateRes.StatusCode);

        // Platform admin deactivates organization -> 204
        using HttpResponseMessage adminDeactRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new UpdateOrganizationRequest(IsActive: false));
        Assert.Equal(HttpStatusCode.NoContent, adminDeactRes.StatusCode);

        // Normal user accesses deactivated organization -> 403
        using HttpResponseMessage normalGet = await normalClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, normalGet.StatusCode);

        // Platform admin can inspect deactivated organization -> 200
        using HttpResponseMessage adminGet = await adminClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);

        // Platform admin reactivates organization -> 204
        using HttpResponseMessage adminReactRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new UpdateOrganizationRequest(IsActive: true));
        Assert.Equal(HttpStatusCode.NoContent, adminReactRes.StatusCode);
    }

    [Fact]
    public async Task DuplicateSlugReturnsConflict409()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid existingOwnerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        await CreateUserAsync(factory, existingOwnerId, "existing@example.com", "Existing", CustomerRoleId);
        await CreateUserAsync(factory, userId, "user@example.com", "User", CustomerRoleId);
        await SeedOrganizationAsync(factory, existingOwnerId, "conflict-corp", OrganizationStatus.Active);

        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        using HttpResponseMessage res = await client.PostAsJsonAsync("/api/v1/auth/organizations/register", Profile("conflict-corp") with { TaxCode = "0399999999" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
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

        using HttpResponseMessage getRes = await client.GetAsync($"/api/v1/auth/organizations/{randomOrgId}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        using HttpResponseMessage approveRes = await client.PatchAsync($"/api/v1/auth/organizations/{randomOrgId}/approve", null);
        Assert.Equal(HttpStatusCode.NotFound, approveRes.StatusCode);
    }

    [Fact]
    public async Task InvalidPayloadsReturnBadRequest400()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, ownerId, "good-org", OrganizationStatus.Active, name: "Good Org");

        using HttpClient client = CreateAuthenticatedClient(factory, adminId, "admin");

        // Invalid slug format khi đăng ký -> 400
        using HttpResponseMessage invalidSlugRes = await client.PostAsJsonAsync("/api/v1/auth/organizations/register", Profile("invalid slug format!"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidSlugRes.StatusCode);

        // Empty PATCH -> 400
        using HttpResponseMessage emptyPatchRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, emptyPatchRes.StatusCode);

        // No-op PATCH -> 400
        using HttpResponseMessage noopRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new UpdateOrganizationRequest(Name: "Good Org", IsActive: true));
        Assert.Equal(HttpStatusCode.BadRequest, noopRes.StatusCode);

        // Reject thiếu lý do -> 400
        using HttpResponseMessage rejectRes = await client.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}/reject", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, rejectRes.StatusCode);
    }

    private static OrganizationProfileRequest Profile(string slug) =>
        new(
            Name: "Rent Express",
            Type: OrganizationType.Supplier,
            TaxCode: "0312345678",
            LegalRepresentativeName: "Nguyen Van A",
            ContactEmail: "contact@rentexpress.vn",
            ContactPhone: "0281234567",
            Address: "12 Nguyen Hue",
            Province: "TP. Ho Chi Minh",
            Ward: "Phuong Sai Gon",
            BusinessLicenseDocumentUrl: "https://files.example.com/license.pdf",
            Slug: slug);

    /// <summary>Dựng sẵn organization + Owner trong DB (thay cho POST /organizations đã bỏ).</summary>
    private static async Task<Guid> SeedOrganizationAsync(
        WebApplicationFactory<Program> factory,
        Guid ownerId,
        string slug,
        OrganizationStatus status,
        string name = "Seeded Org")
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Guid orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = name,
            Slug = slug,
            Type = OrganizationType.Supplier,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow
        });
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = ownerId,
            RoleId = OwnerRoleId,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return orgId;
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
            ["Auth:Jwt:Keys:0:PrivateKeyPem"] = rsa.ExportRSAPrivateKeyPem(),
            ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
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
            ProfileCompletedAtUtc = DateTime.UtcNow,
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
