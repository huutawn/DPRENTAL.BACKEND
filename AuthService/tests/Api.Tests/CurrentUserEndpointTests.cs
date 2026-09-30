using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;

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

public sealed class CurrentUserEndpointTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();

    [Fact]
    public async Task GetMeWithoutTokenReturnsUnauthorized()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedAsync(factory);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMeWithBearerTokenReturnsProfileRolesPermissionsAndOrganizations()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedAsync(factory);
        Guid sessionId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = sessionId,
                UserId = UserId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }
        using HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(UserId, ["Admin"], SessionId: sessionId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, root.GetProperty("id").GetGuid());
        Assert.Equal("tan.len@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Huynh Tan Len", root.GetProperty("fullName").GetString());
        Assert.Equal(["Admin"], root.GetProperty("roles").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["roles.assign", "roles.revoke"],
            root.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
        JsonElement organization = Assert.Single(root.GetProperty("organizations").EnumerateArray());
        Assert.Equal(OrganizationId, organization.GetProperty("organizationId").GetGuid());
        Assert.Equal("acme", organization.GetProperty("slug").GetString());
        Assert.Equal("Owner", organization.GetProperty("roleName").GetString());
    }

    [Fact]
    public async Task GetMeForUnknownUserReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(Guid.NewGuid()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");

        // ??i t? HttpStatusCode.NotFound sang HttpStatusCode.Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
            });
        });
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = UserId,
            Email = "tan.len@example.com",
            FullName = "Huynh Tan Len",
            PasswordHash = "hash",
            CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true
        };
        user.UserRoles.Add(new UserRole { UserId = UserId, Role = CreateRole("Admin", "roles.assign") });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Organization = new Organization
            {
                Id = OrganizationId,
                Name = "Acme",
                Slug = "acme",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Active
            },
            Role = CreateRole("Owner", "roles.revoke"),
            Status = MembershipStatus.Active
        });

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private static Role CreateRole(string name, string permissionCode)
    {
        Role role = new() { Id = Guid.NewGuid(), Name = name };
        role.RolePermissions.Add(new RolePermission
        {
            Role = role,
            Permission = new Permission { Id = Guid.NewGuid(), Code = permissionCode }
        });

        return role;
    }
}
