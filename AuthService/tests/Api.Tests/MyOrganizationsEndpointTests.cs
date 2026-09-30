using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;

using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Application.DTOs.Auth;
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

public sealed class MyOrganizationsEndpointTests
{
    [Fact]
    public async Task GetMyOrganizationsReturnsActiveOrganizationsOnly()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserAsync(factory, userId);

        Guid activeOrgId = Guid.NewGuid();
        Guid invitedOrgId = Guid.NewGuid();
        await SeedOrgMembershipAsync(factory, activeOrgId, userId, "Active Org", "active-org", "Member", MembershipStatus.Active);
        await SeedOrgMembershipAsync(factory, invitedOrgId, userId, "Invited Org", "invited-org", "Member", MembershipStatus.Invited);

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/organizations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement array = doc.RootElement;
        Assert.Equal(1, array.GetArrayLength());
        Assert.Equal(activeOrgId, array[0].GetProperty("organizationId").GetGuid());
        Assert.Equal("Active Org", array[0].GetProperty("name").GetString());
        Assert.Equal("active-org", array[0].GetProperty("slug").GetString());
        Assert.Equal("Member", array[0].GetProperty("roleName").GetString());
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, Guid userId)
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

        IAccessTokenIssuer issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            userId,
            Roles: ["Customer"],
            Permissions: [],
            OrganizationIds: [],
            SessionId: session.Id));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = $"user-{userId:N}@example.com",
            FullName = "Phase C Test User",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            ProfileCompletedAtUtc = DateTime.UtcNow,
            CitizenId = $"079{RandomNumberGenerator.GetInt32(100000000, 999999999)}",
            PhoneNumber = "0901234567",
            DateOfBirth = new DateOnly(1995, 1, 1)
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedOrgMembershipAsync(
        WebApplicationFactory<Program> factory,
        Guid organizationId,
        Guid userId,
        string orgName,
        string orgSlug,
        string roleName,
        MembershipStatus status)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Organization? org = await dbContext.Organizations.FindAsync([organizationId]);
        if (org is null)
        {
            org = new Organization
            {
                Id = organizationId,
                Name = orgName,
                Slug = orgSlug,
                Status = OrganizationStatus.Active,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.Organizations.Add(org);
        }

        Role? role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                Scope = RoleScope.Organization
            };
            dbContext.Roles.Add(role);
        }

        OrganizationMembership membership = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            UserId = userId,
            RoleId = role.Id,
            Status = status,
            JoinedAtUtc = DateTime.UtcNow
        };

        dbContext.OrganizationMemberships.Add(membership);
        await dbContext.SaveChangesAsync();

        return membership.Id;
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
