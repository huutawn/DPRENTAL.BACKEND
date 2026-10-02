using Dprental.AuthService.Application.Clients;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;

using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Application.DTOs.Auth;
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

        ((FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>()).DefaultUserId = ownerId;

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

        // 3. Admin duyệt bằng PATCH /admin/organizations/{id}/approve
        using HttpResponseMessage approveRes = await adminClient.PatchAsync($"/api/v1/auth/admin/organizations/{orgId}/approve", null);
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

        ((FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>()).DefaultUserId = adminOwnerId;

        using HttpResponseMessage registerRes = await adminOwnerClient.PostAsJsonAsync("/api/v1/auth/organizations/register", Profile("platform-rental"));
        OrganizationRegisteredResponse? registered = await registerRes.Content.ReadFromJsonAsync<OrganizationRegisteredResponse>();
        Assert.NotNull(registered);

        using HttpResponseMessage selfApprove = await adminOwnerClient.PatchAsync($"/api/v1/auth/admin/organizations/{registered.OrganizationId}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        using HttpResponseMessage selfReject = await adminOwnerClient.PatchAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{registered.OrganizationId}/reject", new RejectOrganizationRequest("Tự từ chối"));
        Assert.Equal(HttpStatusCode.Forbidden, selfReject.StatusCode);

        using HttpResponseMessage otherApprove = await otherAdminClient.PatchAsync($"/api/v1/auth/admin/organizations/{registered.OrganizationId}/approve", null);
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

        // Route /organizations chỉ còn các route của Owner/User; tạo trực tiếp không còn tồn tại -> 404 hoặc 405.
        using HttpResponseMessage res = await client.PostAsJsonAsync("/api/v1/auth/organizations", new { name = "Direct", slug = "direct", type = 1 });
        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
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
    public async Task PlatformAdminCanSuspendInspectAndActivateOrganization()
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

        // Normal user attempts to suspend -> 403
        using HttpResponseMessage normalUpdateRes = await normalClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/suspend",
            new SuspendOrganizationRequest("Normal user attempt"));
        Assert.Equal(HttpStatusCode.Forbidden, normalUpdateRes.StatusCode);

        // Platform admin suspends organization -> 204
        using HttpResponseMessage adminDeactRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/suspend",
            new SuspendOrganizationRequest("Admin suspension reason"));
        Assert.Equal(HttpStatusCode.NoContent, adminDeactRes.StatusCode);

        // Normal user accesses deactivated organization -> 403
        using HttpResponseMessage normalGet = await normalClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, normalGet.StatusCode);

        // Platform admin can inspect deactivated organization -> 200 via admin route
        using HttpResponseMessage adminGet = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);

        // Platform admin reactivates organization -> 204
        using HttpResponseMessage adminReactRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{orgId}/activate", null);
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

        using HttpResponseMessage getRes = await client.GetAsync($"/api/v1/auth/admin/organizations/{randomOrgId}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        using HttpResponseMessage approveRes = await client.PatchAsync($"/api/v1/auth/admin/organizations/{randomOrgId}/approve", null);
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

        // Empty name -> 400
        using HttpResponseMessage emptyNameRes = await client.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyNameRes.StatusCode);

        // Name > 150 chars -> 400
        using HttpResponseMessage tooLongRes = await client.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", new { name = new string('a', 151) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLongRes.StatusCode);

        // Reject thiếu lý do -> 400
        using HttpResponseMessage rejectRes = await client.PatchAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}/reject", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, rejectRes.StatusCode);
    }

    [Fact]
    public async Task MineEndpointReturns204BeforeRegistrationAnd200WithStatusAfter()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid ownerId = Guid.NewGuid();
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        using HttpClient anonymous = factory.CreateClient();
        using HttpClient ownerClient = CreateAuthenticatedClient(factory, ownerId, "customer");
        const string mineUrl = "/api/v1/auth/organizations/mine";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(mineUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.GetAsync(mineUrl)).StatusCode);

        Guid orgId = await SeedOrganizationAsync(factory, ownerId, "my-waiting-org", OrganizationStatus.Pending);

        using HttpResponseMessage response = await ownerClient.GetAsync(mineUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OrganizationResponse? mine = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.Equal(orgId, mine?.Id);
        Assert.Equal(OrganizationStatus.Pending, mine?.Status);
        Assert.NotNull(mine?.CanRemindAtUtc);
    }

    [Fact]
    public async Task RemindEndpointRejectsEarlyRemindersAndAcceptsOverdueOnes()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid ownerId = Guid.NewGuid();
        Guid outsiderId = Guid.NewGuid();
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        await CreateUserAsync(factory, outsiderId, "outsider@example.com", "Outsider", CustomerRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, ownerId, "waiting-org", OrganizationStatus.Pending);
        string remindUrl = $"/api/v1/auth/organizations/{orgId}/remind";

        using HttpClient anonymous = factory.CreateClient();
        using HttpClient ownerClient = CreateAuthenticatedClient(factory, ownerId, "customer");
        using HttpClient outsiderClient = CreateAuthenticatedClient(factory, outsiderId, "customer");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(remindUrl, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.PostAsync(remindUrl, null)).StatusCode);

        // Vừa nộp xong, còn trong hạn duyệt -> chưa được nhắc.
        using HttpResponseMessage early = await ownerClient.PostAsync(remindUrl, null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        using JsonDocument earlyBody = JsonDocument.Parse(await early.Content.ReadAsStringAsync());
        Assert.Equal("Organization.ReviewReminderTooEarly", earlyBody.RootElement.GetProperty("title").GetString());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = await db.Organizations.SingleAsync(o => o.Id == orgId);
            org.CreatedAtUtc = DateTime.UtcNow.AddHours(-49);
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage overdue = await ownerClient.PostAsync(remindUrl, null);
        Assert.Equal(HttpStatusCode.OK, overdue.StatusCode);
        OrganizationResponse? reminded = await overdue.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(reminded?.LastRemindedAtUtc);
        Assert.True(reminded.CanRemindAtUtc > DateTime.UtcNow);
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
            BusinessLicenseDocumentMediaId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
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

    [Fact]
    public async Task PagedGetEndpointsBindAndApplySearch()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid adminId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await CreateUserAsync(factory, targetId, "target@example.com", "Target User", CustomerRoleId);

        Guid blueId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization blue = new() { Id = blueId, Name = "Blue Rentals", Slug = "blue-rentals", Status = OrganizationStatus.Active };
            Organization red = new() { Id = Guid.NewGuid(), Name = "Red Rentals", Slug = "red-rentals", Status = OrganizationStatus.Pending };
            db.Organizations.AddRange(blue, red);
            db.OrganizationMemberships.AddRange(
                new OrganizationMembership { Id = Guid.NewGuid(), Organization = blue, UserId = adminId, RoleId = OwnerRoleId, Status = MembershipStatus.Active },
                new OrganizationMembership { Id = Guid.NewGuid(), Organization = blue, UserId = targetId, RoleId = MemberRoleId, Status = MembershipStatus.Active });
            await db.SaveChangesAsync();
        }

        using HttpClient admin = factory.CreateClient();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Session session = new()
            {
                Id = Guid.NewGuid(),
                UserId = adminId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                RevokedAtUtc = null
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();

            IAccessTokenIssuer issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
            IssuedAccessToken adminToken = issuer.Issue(new AccessTokenRequest(adminId, ["admin"], [Permissions.Users.Read, Permissions.Organizations.Read], SessionId: session.Id));
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.Value);
        }
        using HttpResponseMessage usersResponse = await admin.GetAsync("/api/v1/auth/admin/users?search=TARGET&Page=1&PageSize=1");
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        PagedResult<AdminUserListItemDto>? users = await usersResponse.Content.ReadFromJsonAsync<PagedResult<AdminUserListItemDto>>();
        Assert.NotNull(users);
        Assert.Equal(targetId, Assert.Single(users.Items).Id);
        Assert.Equal(1, users.TotalCount);

        using HttpResponseMessage organizationsResponse = await admin.GetAsync("/api/v1/auth/admin/organizations?search=BLUE&status=Active");
        Assert.Equal(HttpStatusCode.OK, organizationsResponse.StatusCode);
        PagedResult<OrganizationSummaryResponse>? organizations = await organizationsResponse.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(organizations);
        Assert.Equal(blueId, Assert.Single(organizations.Items).Id);
        Assert.Equal(1, organizations.TotalCount);

        string tooLong = new('x', PageRequest.MaxSearchLength + 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/v1/auth/admin/users?search={tooLong}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/v1/auth/admin/organizations?search={tooLong}")).StatusCode);
        using HttpClient target = CreateAuthenticatedClient(factory, targetId, "customer");
        Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync("/api/v1/auth/admin/users?search=admin")).StatusCode);
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
            builder.UseSetting("MediaService:ApiKey", "test-media-key");
            builder.UseSetting("MediaService:PublicGatewayUrl", "http://gateway.example.test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new FakeMediaServiceClient { AutoVerify = true });
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
        Permission pUsersRead = new() { Id = Guid.NewGuid(), Code = Permissions.Users.Read };

        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pCreate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pUpdate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pMemRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pMemManage });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pPlatform });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pAssign });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRevoke });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pUsersRead });

        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pRead });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pUpdate });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pMemRead });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pMemManage });

        memberRole.RolePermissions.Add(new RolePermission { Role = memberRole, Permission = pRead });
        memberRole.RolePermissions.Add(new RolePermission { Role = memberRole, Permission = pMemRead });

        db.Permissions.AddRange(pCreate, pRead, pUpdate, pMemRead, pMemManage, pPlatform, pAssign, pRevoke, pUsersRead);
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
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            RevokedAtUtc = null
        };
        dbContext.Sessions.Add(session);
        dbContext.SaveChanges();

        List<string> permissions = dbContext.UserRoles
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToList();

        IAccessTokenIssuer issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, roles, Permissions: permissions, SessionId: session.Id));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }
}
