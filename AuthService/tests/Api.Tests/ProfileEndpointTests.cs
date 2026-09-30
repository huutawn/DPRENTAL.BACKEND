using Dprental.AuthService.Application.Clients;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;

using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
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

public sealed class ProfileEndpointTests
{
    [Fact]
    public async Task CompleteProfileFirstTimeReturns201AndSubsequentCallsReturn409()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserAsync(factory, userId, isCompleted: false);
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        CompleteProfileRequest request = new(
            CitizenId: "079203001234",
            PhoneNumber: "0901234567",
            DateOfBirth: new DateOnly(2000, 1, 1),
            FullName: "Complete Name",
            AvatarMediaId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/users/profile", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("isProfileCompleted").GetBoolean());
        Assert.Equal("********1234", root.GetProperty("maskedCitizenId").GetString());
        Assert.Equal("Complete Name", root.GetProperty("fullName").GetString());
        Assert.Equal("0901234567", root.GetProperty("phoneNumber").GetString());

        // GET /users/me should also show completed
        using HttpResponseMessage getResponse = await client.GetAsync("/api/v1/auth/users/me");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using JsonDocument getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.True(getDoc.RootElement.GetProperty("isProfileCompleted").GetBoolean());
        Assert.Equal("********1234", getDoc.RootElement.GetProperty("maskedCitizenId").GetString());

        // Calling POST /users/me again should return 409 Conflict
        using HttpResponseMessage secondResponse = await client.PostAsJsonAsync("/api/v1/auth/users/profile", request);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        using JsonDocument secondDoc = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal("Profile.AlreadyCompleted", secondDoc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task IncompleteProfileCannotRegisterOrganizationReturns403()
    {
        Guid userId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserAsync(factory, userId, isCompleted: false);
        await SeedOwnerRoleAsync(factory);
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        OrganizationProfileRequest request = new(
            Name: "Test Org",
            TaxCode: "0123456789",
            Type: OrganizationType.Supplier,
            LegalRepresentativeName: "Test Rep",
            ContactEmail: "org@example.com",
            ContactPhone: "0901234567",
            Address: "123 Street",
            Province: "HCM",
            Ward: "Ben Nghe",
            BusinessLicenseDocumentMediaId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/register", request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Profile.NotCompleted", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UpdateProfileUpdatesFieldsAndRemoveAvatarClearsAvatar()
    {
        Guid userId = Guid.NewGuid();
        Guid oldMediaId = Guid.NewGuid();
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedUserAsync(factory, userId, isCompleted: true, avatarMediaId: oldMediaId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        UpdateProfileRequest request = new(
            FullName: "Patched Name",
            RemoveAvatar: true);

        using HttpResponseMessage response = await client.PatchAsJsonAsync("/api/v1/auth/users/profile", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Patched Name", document.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("avatarMediaId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("avatarUrl").ValueKind);
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

        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, SessionId: session.Id));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        bool isCompleted,
        Guid? avatarMediaId = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = $"user-{userId:N}@example.com",
            FullName = "Seed User",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            AvatarMediaId = avatarMediaId,
            IsActive = true,
            ProfileCompletedAtUtc = isCompleted ? DateTime.UtcNow : null,
            CitizenId = isCompleted ? "079203009999" : null,
            PhoneNumber = isCompleted ? "0909999999" : null,
            DateOfBirth = isCompleted ? new DateOnly(1995, 5, 5) : null
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        ((FakeMediaServiceClient)scope.ServiceProvider.GetRequiredService<IMediaServiceClient>()).DefaultUserId = userId;
    }

    private static async Task SeedOwnerRoleAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Guid ownerRoleId = new("a1010000-0000-0000-0000-000000000001");
        if (!await dbContext.Roles.AnyAsync(r => r.Id == ownerRoleId))
        {
            dbContext.Roles.Add(new Role { Id = ownerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization });
            await dbContext.SaveChangesAsync();
        }
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
                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new FakeMediaServiceClient { AutoVerify = true });
            });
        });
    }
}
