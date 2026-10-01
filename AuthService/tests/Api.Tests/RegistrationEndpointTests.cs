using Dprental.AuthService.Application.Clients;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.IServices;

using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class RegistrationEndpointTests
{
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PostRegisterWithoutCitizenIdReturnsCreated()
    {
        // Đăng ký customer không còn thu thập CCCD — request tối thiểu vẫn phải thành công.
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest("customer@example.com", "Matkhau123!", "Customer"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("a@b", "Matkhau123!")]                    // tên miền không có đuôi
    [InlineData("us..er@example.com", "Matkhau123!")]     // hai dấu chấm liền nhau
    [InlineData("customer@example.com", "12345678")]      // mật khẩu phổ biến
    [InlineData("customer@example.com", "customer-2026")] // mật khẩu chứa phần trước @ của email
    public async Task PostRegisterWithMalformedEmailOrWeakPasswordReturnsBadRequest(string email, string password)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, password, "Customer"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostRegisterWithOptionalProfileFieldsReturnsCreated()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest("customer2@example.com", "Matkhau123!", "Customer", "0901234567", new DateOnly(2000, 1, 1)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostRegisterIgnoresAvatarAndDoesNotSetAvatarMediaId()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/auth/register",
            new StringContent(
                """{"email":"customer_avatar@example.com","password":"Matkhau123!","fullName":"Customer","avatarUrl":"https://example.com/avatar.png","avatarMediaId":"11111111-1111-1111-1111-111111111111"}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User? createdUser = await db.Users.SingleOrDefaultAsync(u => u.Email == "customer_avatar@example.com");
        Assert.NotNull(createdUser);
        Assert.Null(createdUser.AvatarMediaId);
    }

    [Fact]
    public async Task PostOrganizationsRegisterRequiresAuthentication()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/register", CreateProfile());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostOrganizationsRegisterCreatesPendingOrganizationForLoggedInUser()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedOwnerRoleAsync(factory);
        Guid userId = await SeedUserAsync(factory, "owner@example.com");
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/register", CreateProfile());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid organizationId = body.RootElement.GetProperty("organizationId").GetGuid();

        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Organization org = await dbContext.Organizations.SingleAsync(o => o.Id == organizationId);
        Assert.Equal(OrganizationStatus.Pending, org.Status);
    }

    [Fact]
    public async Task PostOrganizationsRegisterWithInvalidTaxCodeReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedOwnerRoleAsync(factory);
        Guid userId = await SeedUserAsync(factory, "owner@example.com");
        using HttpClient client = CreateAuthenticatedClient(factory, userId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/organizations/register", CreateProfile() with { TaxCode = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static OrganizationProfileRequest CreateProfile() =>
        new(
            Name: "Dong A Rental",
            Type: OrganizationType.Supplier,
            TaxCode: "0312345678",
            LegalRepresentativeName: "Nguyen Van A",
            ContactEmail: "contact@donga.vn",
            ContactPhone: "0281234567",
            Address: "12 Nguyen Hue",
            Province: "TP. Ho Chi Minh",
            Ward: "Phuong Sai Gon",
            BusinessLicenseDocumentMediaId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static async Task SeedOwnerRoleAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        dbContext.Roles.Add(new Role { Id = OwnerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedUserAsync(WebApplicationFactory<Program> factory, string email)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        User user = new() { Id = Guid.NewGuid(), Email = email, FullName = "Owner", PasswordHash = "hash", IsActive = true, ProfileCompletedAtUtc = DateTime.UtcNow };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        ((FakeMediaServiceClient)scope.ServiceProvider.GetRequiredService<IMediaServiceClient>()).DefaultUserId = user.Id;
        return user.Id;
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
            ["Auth:AdminBootstrap:CreationKey"] = "test-admin-bootstrap-secret-key-12345"
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
}
