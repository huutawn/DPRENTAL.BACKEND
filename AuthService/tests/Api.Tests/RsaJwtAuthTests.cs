using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DependencyInjection;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Dprental.AuthService.Domain.Validators;
using Dprental.AuthService.Application.IServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class RsaJwtAuthTests
{
    private static readonly RsaPemPair ActivePair = CreateKeyPair();
    private static readonly RsaPemPair RetiredPair = CreateKeyPair();

    [Fact]
    public void IssueCreatesRs256TokenWithKeyIdAndNormalizedRoles()
    {
        JwtOptions options = CreateValidOptions();
        using RsaJwtKeyRing keyRing = new(options);
        DateTimeOffset now = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);
        RsaAccessTokenIssuer issuer = new(Options.Create(options), keyRing, new FixedTimeProvider(now));
        Guid subjectId = Guid.NewGuid();

        IssuedAccessToken result = issuer.Issue(new AccessTokenRequest(subjectId, ["admin", " admin ", "", "staff"]));
        JwtSecurityToken rawToken = new JwtSecurityTokenHandler().ReadJwtToken(result.Value);
        ClaimsPrincipal principal = ValidateToken(result.Value, options, keyRing);

        Assert.Equal(SecurityAlgorithms.RsaSha256, rawToken.Header.Alg);
        Assert.Equal(options.ActiveKeyId, rawToken.Header.Kid);
        Assert.Equal(subjectId.ToString("D"), principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
        Assert.Equal(["adm", "staff"], principal.FindAll(JwtClaimCatalog.Claims.Role).Select(claim => claim.Value));
        Assert.NotNull(principal.FindFirst(JwtRegisteredClaimNames.Jti));
        Assert.NotNull(principal.FindFirst(JwtRegisteredClaimNames.Iat));
        Assert.Equal(now.AddMinutes(options.AccessTokenLifetimeMinutes), result.ExpiresAtUtc);
    }

    [Fact]
    public void KeyRingVerifiesTokensFromRetiredPublicKey()
    {
        JwtOptions options = CreateValidOptions(
        [
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = ActivePair.PublicKeyPem, PrivateKeyPem = ActivePair.PrivateKeyPem },
            new JwtKeyOptions { KeyId = "retired", PublicKeyPem = RetiredPair.PublicKeyPem }
        ]);
        using RsaJwtKeyRing keyRing = new(options);
        string retiredToken = CreateTokenSignedBy(RetiredPair, "retired", options);

        ClaimsPrincipal principal = ValidateToken(retiredToken, options, keyRing);

        Assert.Equal("retired-subject", principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
    }

    [Fact]
    public void KeyRingLoadsPemFromFilePaths()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string publicKeyPath = Path.Combine(temporaryDirectory, "public.pem");
        string privateKeyPath = Path.Combine(temporaryDirectory, "private.pem");
        File.WriteAllText(publicKeyPath, ActivePair.PublicKeyPem);
        File.WriteAllText(privateKeyPath, ActivePair.PrivateKeyPem);

        try
        {
            JwtOptions options = CreateValidOptions(
            [
                new JwtKeyOptions
                {
                    KeyId = "current",
                    PublicKeyPem = publicKeyPath,
                    PrivateKeyPem = privateKeyPath
                }
            ]);

            using RsaJwtKeyRing keyRing = new(options);

            Assert.Single(keyRing.ResolveVerificationKeys("current"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void ValidationRejectsSymmetricTokenAndUnknownKeyId()
    {
        JwtOptions options = CreateValidOptions();
        using RsaJwtKeyRing keyRing = new(options);
        string symmetricToken = CreateSymmetricToken(options);
        string unknownKeyToken = CreateTokenSignedBy(ActivePair, "unknown-key", options);

        Assert.ThrowsAny<SecurityTokenException>(() => ValidateToken(symmetricToken, options, keyRing));
        Assert.ThrowsAny<SecurityTokenException>(() => ValidateToken(unknownKeyToken, options, keyRing));
    }

    [Fact]
    public void BearerValidationRejectsExpiredAndWrongAudienceTokens()
    {
        JwtOptions options = CreateValidOptions();
        ServiceCollection services = [];
        services.AddAuthApplication(CreateConfiguration(options));
        services.AddJwtBearerSecurity();
        using ServiceProvider provider = services.BuildServiceProvider();
        TokenValidationParameters parameters = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;
        string expiredToken = CreateTokenSignedBy(ActivePair, options.ActiveKeyId, options, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1));
        JwtOptions wrongAudienceOptions = new()
        {
            Issuer = options.Issuer,
            Audience = "unexpected-client",
            AccessTokenLifetimeMinutes = options.AccessTokenLifetimeMinutes,
            ActiveKeyId = options.ActiveKeyId,
            Keys = options.Keys
        };
        string wrongAudienceToken = CreateTokenSignedBy(ActivePair, options.ActiveKeyId, wrongAudienceOptions, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(expiredToken, parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(wrongAudienceToken, parameters, out _));
    }

    [Theory]
    [InlineData("missing-active-key")]
    [InlineData("")]
    public void OptionsValidatorRejectsInvalidActiveKey(string activeKeyId)
    {
        JwtOptions options = CreateValidOptions();
        options = new JwtOptions
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            AccessTokenLifetimeMinutes = options.AccessTokenLifetimeMinutes,
            ActiveKeyId = activeKeyId,
            Keys = options.Keys
        };

        ValidateOptionsResult result = new JwtOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsMismatchedOrDuplicateKeys()
    {
        JwtOptions mismatchedKeys = CreateValidOptions(
        [
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = ActivePair.PublicKeyPem, PrivateKeyPem = RetiredPair.PrivateKeyPem }
        ]);
        JwtOptions duplicateKeys = CreateValidOptions(
        [
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = ActivePair.PublicKeyPem, PrivateKeyPem = ActivePair.PrivateKeyPem },
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = RetiredPair.PublicKeyPem }
        ]);

        Assert.True(new JwtOptionsValidator().Validate(null, mismatchedKeys).Failed);
        Assert.True(new JwtOptionsValidator().Validate(null, duplicateKeys).Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsMissingPrivateKeyAndInvalidPem()
    {
        JwtOptions missingPrivateKey = CreateValidOptions(
        [
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = ActivePair.PublicKeyPem }
        ]);
        JwtOptions invalidPem = CreateValidOptions(
        [
            new JwtKeyOptions { KeyId = "current", PublicKeyPem = "not-a-pem", PrivateKeyPem = ActivePair.PrivateKeyPem }
        ]);

        Assert.True(new JwtOptionsValidator().Validate(null, missingPrivateKey).Failed);
        Assert.True(new JwtOptionsValidator().Validate(null, invalidPem).Failed);
    }

    [Fact]
    public void JwtBearerConfigurationUsesKeyIdResolverAndRs256Only()
    {
        JwtOptions options = CreateValidOptions();
        ServiceCollection services = [];
        services.AddAuthApplication(CreateConfiguration(options));
        services.AddJwtBearerSecurity();
        using ServiceProvider provider = services.BuildServiceProvider();

        JwtBearerOptions bearerOptions = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.False(bearerOptions.MapInboundClaims);
        Assert.Equal(options.Issuer, bearerOptions.TokenValidationParameters.ValidIssuer);
        Assert.Equal(options.Audience, bearerOptions.TokenValidationParameters.ValidAudience);
        Assert.Contains(SecurityAlgorithms.RsaSha256, bearerOptions.TokenValidationParameters.ValidAlgorithms);
        Assert.NotNull(bearerOptions.TokenValidationParameters.IssuerSigningKeyResolver);
    }

    [Fact]
    public async Task JwksEndpointPublishesOnlyPublicVerificationKeys()
    {
        JwtOptions options = CreateValidOptions();
        using WebApplicationFactory<Program> factory = new();
        using WebApplicationFactory<Program> configuredFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddConfiguration(CreateConfiguration(options))));
        using HttpClient client = configuredFactory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/.well-known/jwks.json");
        string payload = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement key = document.RootElement.GetProperty("keys")[0];

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal(options.ActiveKeyId, key.GetProperty("kid").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.True(key.TryGetProperty("n", out _));
        Assert.True(key.TryGetProperty("e", out _));
        Assert.False(key.TryGetProperty("d", out _));
        Assert.DoesNotContain(ActivePair.PrivateKeyPem, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LivenessEndpointReturnsSuccessWithoutDatabaseAccess()
    {
        JwtOptions options = CreateValidOptions();
        using WebApplicationFactory<Program> factory = new();
        using WebApplicationFactory<Program> configuredFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddConfiguration(CreateConfiguration(options))));
        using HttpClient client = configuredFactory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/live");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task SwaggerDocumentPublishesBearerCredentialScheme()
    {
        JwtOptions options = CreateValidOptions();
        using WebApplicationFactory<Program> factory = new();
        using WebApplicationFactory<Program> configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddConfiguration(CreateConfiguration(options)));
        });
        using HttpClient client = configuredFactory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("Json Web Token", scheme.GetProperty("bearerFormat").GetString());
    }

    [Fact]
    public async Task JwksEndpointContinuesInboundW3CTrace()
    {
        JwtOptions options = CreateValidOptions();
        ConcurrentQueue<ActivitySnapshot> serverActivities = new();
        ActivityCaptureExporter exporter = new(serverActivities);
        const string inboundTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        using WebApplicationFactory<Program> factory = new();
        using WebApplicationFactory<Program> configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddConfiguration(CreateConfiguration(options)));
            builder.ConfigureServices(services => services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter))));
        });
        // TestServer không phát server Activity; Kestrel mới phản ánh đúng pipeline HTTP của service khi chạy thực tế.
        configuredFactory.UseKestrel(0);
        using HttpClient client = configuredFactory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/.well-known/jwks.json");
        request.Headers.TryAddWithoutValidation("traceparent", inboundTraceParent);

        using HttpResponseMessage response = await SendWithoutClientInstrumentationAsync(client, request);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(ActivityContext.TryParse(inboundTraceParent, null, out ActivityContext inboundContext));
        // AuthService phải tạo server span con của span Gateway để hai service xuất hiện trong cùng trace.
        Assert.Contains(serverActivities, activity =>
            activity.IdFormat == ActivityIdFormat.W3C
            && activity.TraceId == inboundContext.TraceId
            && activity.ParentSpanId == inboundContext.SpanId);
    }

    private static ClaimsPrincipal ValidateToken(string token, JwtOptions options, RsaJwtKeyRing keyRing)
    {
        return new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, keyId, _) => keyRing.ResolveVerificationKeys(keyId),
            ValidateLifetime = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtClaimCatalog.Claims.Role
        }, out _);
    }

    private static async Task<HttpResponseMessage> SendWithoutClientInstrumentationAsync(HttpClient client, HttpRequestMessage request)
    {
        // Client của test không thuộc luồng Gateway; tắt instrumentation tại đây để nó không ghi đè traceparent cần kiểm tra.
        using IDisposable scope = SuppressInstrumentationScope.Begin();
        return await client.SendAsync(request);
    }

    private static string CreateTokenSignedBy(RsaPemPair pair, string keyId, JwtOptions options)
    {
        return CreateTokenSignedBy(pair, keyId, options, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
    }

    private static string CreateTokenSignedBy(RsaPemPair pair, string keyId, JwtOptions options, DateTime notBeforeUtc, DateTime expiresUtc)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(pair.PrivateKeyPem);
        RsaSecurityKey key = new(rsa)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        JwtSecurityToken token = new(
            options.Issuer,
            options.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "retired-subject")],
            notBeforeUtc,
            expiresUtc,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateSymmetricToken(JwtOptions options)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes("this-key-is-only-for-a-negative-test-case")) { KeyId = options.ActiveKeyId };
        JwtSecurityToken token = new(
            options.Issuer,
            options.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "symmetric-subject")],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static JwtOptions CreateValidOptions(IReadOnlyList<JwtKeyOptions>? keys = null)
    {
        return new JwtOptions
        {
            Issuer = "https://auth.example.test",
            Audience = "dprental-clients",
            AccessTokenLifetimeMinutes = 15,
            ActiveKeyId = "current",
            Keys = keys ?? [new JwtKeyOptions { KeyId = "current", PublicKeyPem = ActivePair.PublicKeyPem, PrivateKeyPem = ActivePair.PrivateKeyPem }]
        };
    }

    private static IConfiguration CreateConfiguration(JwtOptions options)
    {
        Dictionary<string, string?> values = new()
        {
            ["Auth:Jwt:Issuer"] = options.Issuer,
            ["Auth:Jwt:Audience"] = options.Audience,
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = options.AccessTokenLifetimeMinutes.ToString(CultureInfo.InvariantCulture),
            ["Auth:Jwt:ActiveKeyId"] = options.ActiveKeyId,
            ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
        };

        for (int index = 0; index < options.Keys.Count; index++)
        {
            JwtKeyOptions key = options.Keys[index];
            values[$"Auth:Jwt:Keys:{index}:KeyId"] = key.KeyId;
            values[$"Auth:Jwt:Keys:{index}:PublicKeyPem"] = key.PublicKeyPem;
            values[$"Auth:Jwt:Keys:{index}:PrivateKeyPem"] = key.PrivateKeyPem;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static RsaPemPair CreateKeyPair()
    {
        using RSA rsa = RSA.Create(2048);
        return new RsaPemPair(rsa.ExportRSAPrivateKeyPem(), rsa.ExportRSAPublicKeyPem());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record RsaPemPair(string PrivateKeyPem, string PublicKeyPem);

    private sealed record ActivitySnapshot(ActivityTraceId TraceId, ActivitySpanId ParentSpanId, ActivityIdFormat IdFormat);

    private sealed class ActivityCaptureExporter(ConcurrentQueue<ActivitySnapshot> activities) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch)
            {
                if (activity.Kind == ActivityKind.Server)
                {
                    activities.Enqueue(new ActivitySnapshot(activity.TraceId, activity.ParentSpanId, activity.IdFormat));
                }
            }

            return ExportResult.Success;
        }
    }
}
