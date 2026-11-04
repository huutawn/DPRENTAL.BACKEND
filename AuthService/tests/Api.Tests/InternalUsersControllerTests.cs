using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
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

public sealed class InternalUsersControllerTests
{
    private const string ValidKey = AuthWebApplicationFactory.DefaultInternalApiKey;

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
                    ["InternalAuthentication:ApiKey"] = ValidKey
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

    [Fact]
    public async Task NotificationContactsBatchMissingApiKeyReturns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        var request = new NotificationContactBatchRequest([Guid.NewGuid()]);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/internal/users/notification-contacts:batch", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NotificationContactsBatchValidApiKeyReturnsMatchingContacts()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidKey);

        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        // Seed users into DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.Add(new User
            {
                Id = user1Id,
                Email = "user1@example.com",
                FullName = "User One",
                PasswordHash = "hash1",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            db.Users.Add(new User
            {
                Id = user2Id,
                Email = "user2@example.com",
                FullName = "User Two",
                PasswordHash = "hash2",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var request = new NotificationContactBatchRequest([user1Id, user2Id, Guid.NewGuid()]);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/internal/users/notification-contacts:batch", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NotificationContactBatchResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result.Contacts.Count);

        var c1 = result.Contacts.FirstOrDefault(c => c.UserId == user1Id);
        Assert.NotNull(c1);
        Assert.Equal("user1@example.com", c1.Email);
        Assert.True(c1.IsActive);
        Assert.True(c1.IsEmailVerified);

        var c2 = result.Contacts.FirstOrDefault(c => c.UserId == user2Id);
        Assert.NotNull(c2);
        Assert.Equal("user2@example.com", c2.Email);
        Assert.False(c2.IsActive);
    }
}
