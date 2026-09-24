using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class AdminBootstrapEndpointTests
{
    private const string ConfiguredBootstrapKey = "test-admin-bootstrap-secret-key-12345";
    private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");

    [Fact]
    public async Task PostAdminMissingKeyHeaderReturnsUnauthorized401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        using HttpClient client = factory.CreateClient();

        RegisterRequest request = new("admin1@example.com", "Password123!", "Admin One");
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/admins", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(AuthErrorCode.AdminBootstrap.InvalidKey, problem.Title);
    }

    [Fact]
    public async Task PostAdminWrongKeyHeaderReturnsUnauthorized401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage requestMessage = new(HttpMethod.Post, "/api/v1/auth/admins")
        {
            Content = JsonContent.Create(new RegisterRequest("admin2@example.com", "Password123!", "Admin Two"))
        };
        requestMessage.Headers.Add("X-Admin-Creation-Key", "wrong-secret-key");

        using HttpResponseMessage response = await client.SendAsync(requestMessage);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(AuthErrorCode.AdminBootstrap.InvalidKey, problem.Title);
    }

    [Fact]
    public async Task PostAdminValidKeyCreatesAdminUserAndAssignsAdminRole()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage requestMessage = new(HttpMethod.Post, "/api/v1/auth/admins")
        {
            Content = JsonContent.Create(new RegisterRequest("superadmin@example.com", "Password123!", "Super Admin"))
        };
        requestMessage.Headers.Add("X-Admin-Creation-Key", ConfiguredBootstrapKey);

        using HttpResponseMessage response = await client.SendAsync(requestMessage);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AccountCreatedResponse? created = await response.Content.ReadFromJsonAsync<AccountCreatedResponse>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.AccountId);

        // Verify in DB that user has admin role
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User? dbUser = await db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == created.AccountId);
        Assert.NotNull(dbUser);
        Assert.Equal("superadmin@example.com", dbUser.Email);
        UserRole userRole = Assert.Single(dbUser.UserRoles);
        Assert.Equal(AdminRoleId, userRole.RoleId);
    }

    [Fact]
    public async Task PostAdminDuplicateEmailReturnsConflict409()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        using HttpClient client = factory.CreateClient();

        // Create first admin
        HttpRequestMessage req1 = new(HttpMethod.Post, "/api/v1/auth/admins")
        {
            Content = JsonContent.Create(new RegisterRequest("duplicate@example.com", "Password123!", "Admin 1"))
        };
        req1.Headers.Add("X-Admin-Creation-Key", ConfiguredBootstrapKey);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        // Attempt second admin with same email
        HttpRequestMessage req2 = new(HttpMethod.Post, "/api/v1/auth/admins")
        {
            Content = JsonContent.Create(new RegisterRequest("duplicate@example.com", "Password123!", "Admin 2"))
        };
        req2.Headers.Add("X-Admin-Creation-Key", ConfiguredBootstrapKey);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

        ProblemDetails? problem = await res2.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(AuthErrorCode.User.EmailAlreadyExists, problem.Title);
    }

    [Fact]
    public async Task PostAdminMissingAdminRoleReturnsNotFound404AndRollsBack()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        // Do NOT seed admin role in database
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage req = new(HttpMethod.Post, "/api/v1/auth/admins")
        {
            Content = JsonContent.Create(new RegisterRequest("orphan@example.com", "Password123!", "Orphan Admin"))
        };
        req.Headers.Add("X-Admin-Creation-Key", ConfiguredBootstrapKey);
        using HttpResponseMessage res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        ProblemDetails? problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(AuthErrorCode.Role.NotFound, problem.Title);

        // Verify that user was NOT created (rollback)
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        bool userExists = await db.Users.AnyAsync(u => u.Email == "orphan@example.com");
        Assert.False(userExists);
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
            ["Auth:AdminBootstrap:CreationKey"] = ConfiguredBootstrapKey
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

        Role adminRole = new() { Id = AdminRoleId, Name = SystemRoleCode.Admin, Scope = RoleScope.Global };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
    }
}
