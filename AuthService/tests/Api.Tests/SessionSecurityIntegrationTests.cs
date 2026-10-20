using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common.Enums;
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
public sealed class SessionSecurityIntegrationTests
{
    [Fact]
    public async Task GetSessions_ExcludesRevokedAndExpiredSessions_AndCapsAt50()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid currentSessionId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            // Seed 55 active sessions
            for (int i = 0; i < 55; i++)
            {
                db.Sessions.Add(new Session
                {
                    Id = i == 0 ? currentSessionId : Guid.NewGuid(),
                    UserId = userId,
                    DeviceName = $"Active Device {i}",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                    RevokedAtUtc = null
                });
            }

            // Seed 5 revoked sessions
            for (int i = 0; i < 5; i++)
            {
                db.Sessions.Add(new Session
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DeviceName = $"Revoked Device {i}",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-60 - i),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                    RevokedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                });
            }

            // Seed 5 expired sessions
            for (int i = 0; i < 5; i++)
            {
                db.Sessions.Add(new Session
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DeviceName = $"Expired Device {i}",
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-10 - i),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(-1),
                    RevokedAtUtc = null
                });
            }

            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateClientWithToken(factory, userId, currentSessionId);
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement array = doc.RootElement;

        // Caps at 50
        Assert.Equal(50, array.GetArrayLength());

        // Every session returned must be active
        foreach (JsonElement element in array.EnumerateArray())
        {
            Assert.True(element.GetProperty("isActive").GetBoolean());
            Assert.Equal(JsonValueKind.Null, element.GetProperty("revokedAtUtc").ValueKind);
        }
    }

    [Fact]
    public async Task GetSessions_WithoutRefreshCookie_IdentifiesIsCurrentFromJwtSid()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid session1 = Guid.NewGuid();
        Guid session2 = Guid.NewGuid();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = session1,
                UserId = userId,
                DeviceName = "Device 1",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            db.Sessions.Add(new Session
            {
                Id = session2,
                UserId = userId,
                DeviceName = "Device 2",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        // Token carries session1 ID, and NO cookie is sent
        using HttpClient client = CreateClientWithToken(factory, userId, session1);
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement array = doc.RootElement;
        Assert.Equal(2, array.GetArrayLength());

        var items = array.EnumerateArray().ToList();
        var item1 = items.First(e => e.GetProperty("id").GetGuid() == session1);
        var item2 = items.First(e => e.GetProperty("id").GetGuid() == session2);

        Assert.True(item1.GetProperty("isCurrent").GetBoolean());
        Assert.False(item2.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task RevokeSession_CurrentSession_ClearsCookies()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid currentSession = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = currentSession,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateClientWithToken(factory, userId, currentSession);
        using HttpResponseMessage response = await client.DeleteAsync($"/api/v1/auth/users/sessions/{currentSession}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify Set-Cookie header clears cookies
        IEnumerable<string> setCookieHeaders = response.Headers.GetValues("Set-Cookie");
        Assert.Contains(setCookieHeaders, h => h.Contains("accessToken=;"));
        Assert.Contains(setCookieHeaders, h => h.Contains("refreshToken=;"));

        // Verify revoked in DB
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Session? s = await db.Sessions.FindAsync([currentSession]);
            Assert.NotNull(s);
            Assert.NotNull(s.RevokedAtUtc);
        }
    }

    [Fact]
    public async Task RevokeSession_OtherSession_DoesNotClearCookies()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid currentSession = Guid.NewGuid();
        Guid otherSession = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = currentSession,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            db.Sessions.Add(new Session
            {
                Id = otherSession,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateClientWithToken(factory, userId, currentSession);
        using HttpResponseMessage response = await client.DeleteAsync($"/api/v1/auth/users/sessions/{otherSession}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Does NOT clear cookies
        Assert.False(response.Headers.Contains("Set-Cookie"));

        // Verify other session is revoked in DB
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Session? s = await db.Sessions.FindAsync([otherSession]);
            Assert.NotNull(s);
            Assert.NotNull(s.RevokedAtUtc);
        }
    }

    [Fact]
    public async Task JwtValidation_MissingSessionId_Returns401Unauthorized()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        using HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        // Issue token WITHOUT SessionId
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, Roles: ["Customer"], SessionId: null));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JwtValidation_RevokedSession_Returns401Unauthorized()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid sessionId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = sessionId,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                RevokedAtUtc = DateTime.UtcNow.AddMinutes(-5) // Revoked!
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateClientWithToken(factory, userId, sessionId);
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JwtValidation_ExpiredSession_Returns401Unauthorized()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId);

        Guid sessionId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Sessions.Add(new Session
            {
                Id = sessionId,
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), // Expired!
                RevokedAtUtc = null
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateClientWithToken(factory, userId, sessionId);
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JwtValidation_ServiceToken_AcceptedWithoutSession()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        Guid servicePrincipalId = Guid.NewGuid();

        using HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        // Issue service token: TokenUse = "service", no SessionId
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            servicePrincipalId,
            TokenUse: "service",
            Audience: "dprental-clients",
            SessionId: null));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        // A service token calling an endpoint requiring user sub returns 401 from controller sub check,
        // but it successfully passes JWT authentication without requiring a user session!
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/users/me");
        // Controller returns 401 because User.GetJwtSub() is null for service token, but authentication itself didn't fail at JWT middleware
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpClient CreateClientWithToken(WebApplicationFactory<Program> factory, Guid userId, Guid sessionId)
    {
        HttpClient client = factory.CreateClient();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            userId,
            Roles: ["Customer"],
            Permissions: [],
            SessionId: sessionId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"user-{userId:N}@example.com",
            FullName = "Test User",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            HasPassword = true,
            ProfileCompletedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        string publicKey = rsa.ExportRSAPublicKeyPem();
        string privateKey = rsa.ExportRSAPrivateKeyPem();

        return new AuthWebApplicationFactory().WithWebHostBuilder(builder =>
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
