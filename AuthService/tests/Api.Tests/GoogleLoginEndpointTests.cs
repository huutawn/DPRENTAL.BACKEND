using Dprental.AuthService.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;

using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Common.Utils;
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
    private const string DevelopmentFrontendOrigin = "https://dev-app.example.test";

    [Fact]
    public async Task GoogleLoginWhenNotConfiguredReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: false);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/google/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GoogleExchangeWhenNotConfiguredReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: false);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/google/exchange", new { ticket = "ticket" });

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
    public async Task GoogleLoginUsesTrustedForwardedOriginForGoogleRedirectUri()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"/api/v1/auth/google/login?returnUrl={Uri.EscapeDataString(FrontendOrigin)}");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "dev-api.lazyops.cloud");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location ?? throw new InvalidOperationException("Missing Location header.");
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("https://dev-api.lazyops.cloud/signin-google", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task GoogleRemoteFailureRedirectsToBffWithError()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        using HttpClient client = CreateClient(factory);
        using HttpResponseMessage login = await client.GetAsync(
            $"/api/v1/auth/google/login?returnUrl={Uri.EscapeDataString(FrontendOrigin)}");
        Uri authorizationRequest = login.Headers.Location ?? throw new InvalidOperationException("Missing Google authorization URL.");
        string state = QueryHelpers.ParseQuery(authorizationRequest.Query)["state"].ToString();

        using HttpResponseMessage callback = await client.GetAsync(
            $"/signin-google?error=access_denied&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Uri location = callback.Headers.Location ?? throw new InvalidOperationException("Missing BFF callback URL.");
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal($"{FrontendOrigin}/api/auth/google/callback", location.GetLeftPart(UriPartial.Path));
        Assert.Equal("Auth.ExternalFailed", query["error"].ToString());
        Assert.Equal(FrontendOrigin, query["returnUrl"].ToString());
        Assert.Equal("no-store", callback.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
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
        Assert.Equal("/api/auth/google/callback", location.AbsolutePath);
        Assert.Equal(FrontendOrigin, QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
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

    [Fact]
    public async Task ExchangeValidTicketReturnsTokenPairAndCannotBeReused()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        Guid userId = Guid.NewGuid();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            dbContext.Users.Add(new User
            {
                Id = userId,
                Email = "google-ticket@example.test",
                PasswordHash = "not-used-for-google-login",
                FullName = "Google Ticket",
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });
            await dbContext.SaveChangesAsync();

            IAuthSessionService sessions = scope.ServiceProvider.GetRequiredService<IAuthSessionService>();
            Result<string> ticketResult = await sessions.CreateGoogleLoginTicketAsync(userId);
            Assert.True(ticketResult.IsSuccess);
            string ticket = ticketResult.Value;

            using HttpClient client = CreateClient(factory);
            using HttpResponseMessage first = await client.PostAsJsonAsync("/api/v1/auth/google/exchange", new { ticket });
            AuthTokenResponse? response = await first.Content.ReadFromJsonAsync<AuthTokenResponse>();
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
            Assert.NotNull(response);
            Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(response.RefreshToken));

            using HttpResponseMessage second = await client.PostAsJsonAsync("/api/v1/auth/google/exchange", new { ticket });
            Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        }
    }

    [Fact]
    public async Task ExchangeExpiredOrMissingTicketIsRejected()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleConfigured: true);
        Guid userId = Guid.NewGuid();
        string expiredTicket = RefreshTokenUtility.Create();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            dbContext.Users.Add(new User
            {
                Id = userId,
                Email = "expired-ticket@example.test",
                PasswordHash = "not-used-for-google-login",
                FullName = "Expired Ticket",
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });
            dbContext.GoogleLoginTickets.Add(new GoogleLoginTicket
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TicketHash = RefreshTokenUtility.ComputeHash(expiredTicket),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1)
            });
            await dbContext.SaveChangesAsync();
        }

        using HttpClient client = CreateClient(factory);
        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/v1/auth/google/exchange", new { ticket = " " });
        using HttpResponseMessage expired = await client.PostAsJsonAsync("/api/v1/auth/google/exchange", new { ticket = expiredTicket });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public void BffCallbackResolverSelectsCallbackWithTheReturnUrlOrigin()
    {
        GoogleAuthOptions options = new()
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AllowedReturnUrls = [FrontendOrigin, DevelopmentFrontendOrigin],
            BffCallbackUrls =
            [
                $"{FrontendOrigin}/api/auth/google/callback",
                $"{DevelopmentFrontendOrigin}/api/auth/google/callback"
            ]
        };

        Assert.True(GoogleBffCallbackResolver.IsValidConfiguration(options));
        Assert.True(GoogleBffCallbackResolver.TryResolve($"{DevelopmentFrontendOrigin}/dashboard", options, out string callback));
        Assert.Equal($"{DevelopmentFrontendOrigin}/api/auth/google/callback", callback);
    }

    [Fact]
    public void BffCallbackResolverRejectsMissingOrDuplicateCallbackOrigins()
    {
        GoogleAuthOptions missing = new()
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AllowedReturnUrls = [FrontendOrigin, DevelopmentFrontendOrigin],
            BffCallbackUrls = [$"{FrontendOrigin}/api/auth/google/callback"]
        };
        GoogleAuthOptions duplicate = new()
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AllowedReturnUrls = [FrontendOrigin],
            BffCallbackUrls =
            [
                $"{FrontendOrigin}/api/auth/google/callback",
                $"{FrontendOrigin}/auth/google/callback"
            ]
        };

        Assert.False(GoogleBffCallbackResolver.IsValidConfiguration(missing));
        Assert.False(GoogleBffCallbackResolver.IsValidConfiguration(duplicate));
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
            ["Auth:Google:AllowedReturnUrls:0"] = FrontendOrigin,
            ["Auth:Google:AllowedReturnUrls:1"] = DevelopmentFrontendOrigin,
            ["Auth:Google:BffCallbackUrls:0"] = $"{FrontendOrigin}/api/auth/google/callback",
            ["Auth:Google:BffCallbackUrls:1"] = $"{DevelopmentFrontendOrigin}/api/auth/google/callback",
            ["Auth:Google:ClientId"] = googleConfigured ? "test-client-id" : string.Empty,
            ["Auth:Google:ClientSecret"] = googleConfigured ? "test-client-secret" : string.Empty,
            ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            // Forwarded-header registration reads host configuration during Program startup.
            builder.UseSetting("Auth:ForwardedHeaders:Enabled", "true");
            builder.UseSetting("Auth:ForwardedHeaders:KnownNetworks:0", "127.0.0.0/8");
            builder.UseSetting("Auth:Google:ClientId", googleConfigured ? "test-client-id" : string.Empty);
            builder.UseSetting("Auth:Google:ClientSecret", googleConfigured ? "test-client-secret" : string.Empty);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
            });
        });
    }
}
