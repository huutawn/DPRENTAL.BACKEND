using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;

using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Common.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class SecurityEndpointTests
{
    [Fact]
    public async Task ChangePasswordWithCorrectOldPasswordReturns204AndClearsCookies()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "OldP@ssw0rd123");
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        ChangePasswordRequest request = new(
            OldPassword: "OldP@ssw0rd123",
            NewPassword: "NewP@ssw0rd456");

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/users/change-password", request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify Set-Cookie header contains expired cookies
        IEnumerable<string> setCookieHeaders = response.Headers.GetValues("Set-Cookie");
        Assert.Contains(setCookieHeaders, header => header.Contains("accessToken=;"));
        Assert.Contains(setCookieHeaders, header => header.Contains("refreshToken=;"));
    }

    [Fact]
    public async Task ChangePasswordWithIncorrectOldPasswordReturns401()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "OldP@ssw0rd123");
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        ChangePasswordRequest request = new(
            OldPassword: "WrongPassword999",
            NewPassword: "NewP@ssw0rd456");

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/users/change-password", request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessionsReturnsListOfActiveSessions()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "OldP@ssw0rd123");
        string rawRefreshToken = RefreshTokenUtility.Create();
        await SeedSessionAsync(factory, userId, rawRefreshToken);

        using HttpClient client = CreateAuthenticatedClient(factory, userId);
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookies.RefreshTokenCookieName}={rawRefreshToken}");

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement array = document.RootElement;
        Assert.True(array.GetArrayLength() >= 1);
        JsonElement firstSession = array[0];
        Assert.True(firstSession.GetProperty("isActive").GetBoolean());
        Assert.True(firstSession.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task RevokeSessionRevokesTargetSessionReturns204()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "OldP@ssw0rd123");
        string rawRefreshToken = RefreshTokenUtility.Create();
        Guid sessionId = await SeedSessionAsync(factory, userId, rawRefreshToken);

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.DeleteAsync($"/api/v1/auth/users/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify session is revoked in DB
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Session? session = await dbContext.Sessions.FindAsync([sessionId]);
        Assert.NotNull(session);
        Assert.NotNull(session.RevokedAtUtc);
    }

    [Fact]
    public async Task GetExternalLoginsReturnsLinkedAccounts()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "P@ssw0rd123");
        await SeedExternalLoginAsync(factory, userId, "Google", "user@example.com");

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/external-logins");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement array = document.RootElement;
        Assert.Equal(1, array.GetArrayLength());
        Assert.Equal("Google", array[0].GetProperty("provider").GetString());
        Assert.Equal("user@example.com", array[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task UnlinkExternalLoginWhenUserHasNoPasswordReturns409Conflict()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithoutPasswordAsync(factory, userId);
        await SeedExternalLoginAsync(factory, userId, "Google", "google@example.com");

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.DeleteAsync("/api/v1/auth/users/external-logins/Google");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ExternalLogin.CannotUnlinkLastCredential", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnlinkExternalLoginWhenUserHasPasswordReturns204NoContent()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "P@ssw0rd123");
        await SeedExternalLoginAsync(factory, userId, "Google", "google@example.com");

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.DeleteAsync("/api/v1/auth/users/external-logins/Google");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify link is deleted
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        bool exists = await dbContext.ExternalLogins.AnyAsync(l => l.UserId == userId && l.Provider == "Google");
        Assert.False(exists);
    }

    [Fact]
    public async Task DeactivateAccountWhenLastOwnerReturns409Conflict()
    {
        Guid userId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "P@ssw0rd123");
        await SeedOrganizationWithOwnerAsync(factory, orgId, userId);

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.DeleteAsync("/api/v1/auth/users/deactivate");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("User.LastOwnerOrganization", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DeactivateAccountHappyPathReturns204ClearsCookiesAndDeactivatesUser()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserWithPasswordAsync(factory, userId, "P@ssw0rd123");

        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.DeleteAsync("/api/v1/auth/users/deactivate");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify Set-Cookie header contains expired cookies
        IEnumerable<string> setCookieHeaders = response.Headers.GetValues("Set-Cookie");
        Assert.Contains(setCookieHeaders, header => header.Contains("accessToken=;"));
        Assert.Contains(setCookieHeaders, header => header.Contains("refreshToken=;"));

        // Verify user is inactive in DB
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User? user = await dbContext.Users.FindAsync([userId]);
        Assert.NotNull(user);
        Assert.False(user.IsActive);
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, Guid userId)
    {
        HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IAccessTokenIssuer issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            userId,
            Roles: ["Customer"],
            Permissions: [],
            OrganizationIds: []));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserWithPasswordAsync(WebApplicationFactory<Program> factory, Guid userId, string password)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        IPasswordHasher<User> passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        User user = new()
        {
            Id = userId,
            Email = $"user-{userId:N}@example.com",
            FullName = "Security Test User",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            HasPassword = true,
            ProfileCompletedAtUtc = DateTime.UtcNow,
            CitizenId = "079203001234",
            PhoneNumber = "0901234567",
            DateOfBirth = new DateOnly(1995, 1, 1)
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedUserWithoutPasswordAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = $"google-{userId:N}@example.com",
            FullName = "Google Only User",
            PasswordHash = "dummy-hash",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            HasPassword = false,
            ProfileCompletedAtUtc = DateTime.UtcNow,
            CitizenId = "079203005678",
            PhoneNumber = "0905678901",
            DateOfBirth = new DateOnly(1996, 2, 2)
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExternalLoginAsync(WebApplicationFactory<Program> factory, Guid userId, string provider, string email)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ExternalLogin login = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = provider,
            ProviderSubject = $"sub-{Guid.NewGuid():N}",
            Email = email,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.ExternalLogins.Add(login);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedSessionAsync(WebApplicationFactory<Program> factory, Guid userId, string rawRefreshToken)
    {
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

        RefreshToken token = new()
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            TokenHash = RefreshTokenUtility.ComputeHash(rawRefreshToken),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            RevokedAtUtc = null
        };

        dbContext.Sessions.Add(session);
        dbContext.RefreshTokens.Add(token);
        await dbContext.SaveChangesAsync();

        return session.Id;
    }

    private static async Task SeedOrganizationWithOwnerAsync(WebApplicationFactory<Program> factory, Guid organizationId, Guid ownerUserId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Organization org = new()
        {
            Id = organizationId,
            Name = "Security Org",
            Slug = $"org-{organizationId:N}",
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };

        Guid ownerRoleId = new("a1010000-0000-0000-0000-000000000001");
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
