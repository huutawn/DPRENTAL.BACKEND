using System.Net;
using System.Security.Cryptography;
using Dprental.AuthService.Domain.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class GoogleLoginEndpointTests
{
    private const string FrontendOrigin = "http://localhost:3000";

    [Fact]
    public async Task GoogleLoginWhenNotConfiguredReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: false);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/google/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GoogleLoginRedirectsToGoogleWithPkceAndStateAndCallbackToUs()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/auth/google/login?returnUrl={Uri.EscapeDataString(FrontendOrigin + "/dashboard")}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location ?? throw new InvalidOperationException("Missing Location header.");
        Assert.Equal("accounts.google.com", location.Host);
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("test-client-id", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(StringValues.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(StringValues.IsNullOrEmpty(query["state"]));
        Assert.EndsWith("/signin-google", query["redirect_uri"].ToString(), StringComparison.Ordinal);
        Assert.Contains("email", query["scope"].ToString(), StringComparison.Ordinal);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains(".Correlation.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GoogleLoginWithoutReturnUrlFallsBackToFirstAllowedOrigin()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/google/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("accounts.google.com", response.Headers.Location?.Host);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://localhost:3000.evil.com")]
    [InlineData("//evil.com")]
    public async Task GoogleLoginWithDisallowedReturnUrlReturnsBadRequestAndDoesNotRedirect(string returnUrl)
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/auth/google/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task GoogleCallbackWithoutExternalCookieRedirectsBackWithError()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/auth/google/callback?returnUrl={Uri.EscapeDataString(FrontendOrigin)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location ?? throw new InvalidOperationException("Missing Location header.");
        Assert.Equal("localhost", location.Host);
        Assert.Equal(3000, location.Port);
        Assert.Equal("Auth.ExternalFailed", QueryHelpers.ParseQuery(location.Query)["error"].ToString());
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) ? cookies : [], cookie => cookie.StartsWith("accessToken=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GoogleCallbackWithDisallowedReturnUrlReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/google/callback?returnUrl=https%3A%2F%2Fevil.com");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static WebApplicationFactory<Program> CreateFactory(bool googleConfigured)
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
            ["Auth:Google:AllowedReturnUrls:0"] = FrontendOrigin
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            if (googleConfigured)
            {
                // Đọc lúc đăng ký service trong Program.cs nên phải là host setting, không phải cấu hình nạp trễ.
                builder.UseSetting("Auth:Google:ClientId", "test-client-id");
                builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
            });
        });
    }
}
