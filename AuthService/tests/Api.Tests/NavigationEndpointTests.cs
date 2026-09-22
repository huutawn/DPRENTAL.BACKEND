using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Enums;
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

public sealed class NavigationEndpointTests
{
    [Fact]
    public async Task NavigationIsFilteredByRequestedOrganizationWithoutCrossOrganizationLeakage()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        NavigationFixture fixture = await SeedAsync(factory);
        using HttpClient client = CreateAuthenticatedClient(factory, fixture.UserId);

        using HttpResponseMessage orgAResponse = await client.GetAsync($"/api/v1/auth/navigation?organizationId={fixture.OrganizationAId}");
        using JsonDocument orgADocument = JsonDocument.Parse(await orgAResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, orgAResponse.StatusCode);
        Assert.Equal("no-store", orgAResponse.Headers.CacheControl?.ToString());
        Assert.Equal(fixture.OrganizationAId, orgADocument.RootElement.GetProperty("organizationId").GetGuid());
        Assert.Equal(
            [PermissionCode.IdentityMembersManage, PermissionCode.IdentityUsersRead],
            Permissions(orgADocument.RootElement));
        Assert.Equal(["identity.users", "identity.members"], ChildKeysInOrder(orgADocument.RootElement));
        JsonElement members = orgADocument.RootElement.GetProperty("items")[0].GetProperty("children")[1];
        Assert.Equal("/organizations/{organizationId}/members", members.GetProperty("href").GetString());
        Assert.Equal("users-round", members.GetProperty("icon").GetString());

        using HttpResponseMessage orgBResponse = await client.GetAsync($"/api/v1/auth/navigation?organizationId={fixture.OrganizationBId}");
        using JsonDocument orgBDocument = JsonDocument.Parse(await orgBResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, orgBResponse.StatusCode);
        Assert.Equal(
            [PermissionCode.IdentityRolesRead, PermissionCode.IdentityUsersRead],
            Permissions(orgBDocument.RootElement));
        Assert.Equal(["identity.users", "identity.roles"], ChildKeysInOrder(orgBDocument.RootElement));
        Assert.DoesNotContain(PermissionCode.IdentityMembersManage, Permissions(orgBDocument.RootElement));

        using HttpResponseMessage globalResponse = await client.GetAsync("/api/v1/auth/navigation");
        using JsonDocument globalDocument = JsonDocument.Parse(await globalResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, globalResponse.StatusCode);
        Assert.Equal([PermissionCode.IdentityUsersRead], Permissions(globalDocument.RootElement));
        Assert.Equal(["identity.users"], ChildKeysInOrder(globalDocument.RootElement));
    }

    [Fact]
    public async Task NavigationRejectsUnauthenticatedAndUnscopedOrganizationRequests()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        NavigationFixture fixture = await SeedAsync(factory);
        using HttpClient anonymousClient = factory.CreateClient();

        using HttpResponseMessage anonymous = await anonymousClient.GetAsync("/api/v1/auth/navigation");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpClient authenticatedClient = CreateAuthenticatedClient(factory, fixture.UserId);
        using HttpResponseMessage noMembership = await authenticatedClient.GetAsync($"/api/v1/auth/navigation?organizationId={fixture.UnrelatedOrganizationId}");
        Assert.Equal(HttpStatusCode.Forbidden, noMembership.StatusCode);

        using HttpResponseMessage emptyOrganization = await authenticatedClient.GetAsync("/api/v1/auth/navigation?organizationId=00000000-0000-0000-0000-000000000000");
        Assert.Equal(HttpStatusCode.BadRequest, emptyOrganization.StatusCode);
    }

    private static string[] Permissions(JsonElement root) => root.GetProperty("permissions")
        .EnumerateArray()
        .Select(item => item.GetString())
        .OfType<string>()
        .ToArray();

    private static string[] ChildKeysInOrder(JsonElement root) => root.GetProperty("items")[0]
        .GetProperty("children")
        .EnumerateArray()
        .Select(item => item.GetProperty("key").GetString())
        .OfType<string>()
        .ToArray();

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, Guid userId)
    {
        HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string databaseName = Guid.NewGuid().ToString("N");
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
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        });
    }

    private static async Task<NavigationFixture> SeedAsync(WebApplicationFactory<Program> factory)
    {
        NavigationFixture fixture = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Permission usersRead = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityUsersRead };
        Permission membersManage = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityMembersManage };
        Permission rolesRead = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityRolesRead };
        Role globalRole = RoleWithPermissions("global", RoleScope.Global, usersRead);
        Role organizationARole = RoleWithPermissions("org-a", RoleScope.Organization, membersManage);
        Role organizationBRole = RoleWithPermissions("org-b", RoleScope.Organization, rolesRead);

        dbContext.Users.Add(new User { Id = fixture.UserId, Email = "navigation@example.com", FullName = "Navigation User", IsActive = true });
        dbContext.Organizations.AddRange(
            new Organization { Id = fixture.OrganizationAId, Name = "Organization A", Slug = "navigation-org-a", IsActive = true },
            new Organization { Id = fixture.OrganizationBId, Name = "Organization B", Slug = "navigation-org-b", IsActive = true },
            new Organization { Id = fixture.UnrelatedOrganizationId, Name = "Other Organization", Slug = "navigation-org-other", IsActive = true });
        dbContext.Roles.AddRange(globalRole, organizationARole, organizationBRole);
        dbContext.UserRoles.Add(new UserRole { UserId = fixture.UserId, RoleId = globalRole.Id });
        dbContext.OrganizationMemberships.AddRange(
            new OrganizationMembership { Id = Guid.NewGuid(), UserId = fixture.UserId, OrganizationId = fixture.OrganizationAId, RoleId = organizationARole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow },
            new OrganizationMembership { Id = Guid.NewGuid(), UserId = fixture.UserId, OrganizationId = fixture.OrganizationBId, RoleId = organizationBRole.Id, Status = MembershipStatus.Active, JoinedAtUtc = DateTime.UtcNow });

        await dbContext.SaveChangesAsync();
        return fixture;
    }

    private static Role RoleWithPermissions(string name, RoleScope scope, params Permission[] permissions)
    {
        Role role = new() { Id = Guid.NewGuid(), Name = name, Scope = scope };
        foreach (Permission permission in permissions)
        {
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, Permission = permission });
        }

        return role;
    }

    private sealed record NavigationFixture(Guid UserId, Guid OrganizationAId, Guid OrganizationBId, Guid UnrelatedOrganizationId);
}
