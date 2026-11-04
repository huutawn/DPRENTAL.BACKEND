using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.ReviewService.Api.Security;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// WebApplicationFactory fixture dùng chung cho bộ kiểm thử API review với cơ sở dữ liệu in-memory và client giả lập.
/// </summary>
public sealed class ReviewTestFixture : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    private readonly RSA _rsa = RSA.Create(2048);
    public const string TestKeyId = "review-test-key-01";
    public const string Issuer = "dprental-authservice-dev";
    public const string Audience = "dprental-clients";

    public DateTimeOffset CurrentTime { get; } = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    public TestRentalServiceClient RentalClient { get; } = new();
    public TestMediaServiceClient MediaClient { get; } = new();
    public TestTimeProvider TimeProvider { get; }
    public ReviewTestFixture()
    {
        TimeProvider = new TestTimeProvider(CurrentTime);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReviewServiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ReviewServiceDbContext>>();
            services.RemoveAll<ReviewServiceDbContext>();
            services.AddDbContext<ReviewServiceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IRentalServiceClient>();
            services.AddSingleton<IRentalServiceClient>(RentalClient);

            services.RemoveAll<IMediaServiceClient>();
            services.AddSingleton<IMediaServiceClient>(MediaClient);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = new TestJwksConfigurationManager(_rsa, TestKeyId);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(5),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    NameClaimType = "sub",
                    RoleClaimType = "r"
                };
            });
        });
    }

    public string GenerateJwt(
        Guid userId,
        string? userType = "cus",
        string? role = null,
        string? issuer = Issuer,
        string? audience = Audience,
        bool expired = false,
        RSA? customKey = null,
        string? keyId = TestKeyId)
    {
        var key = new RsaSecurityKey(customKey ?? _rsa) { KeyId = keyId };
        var claims = new List<Claim> { new("sub", userId.ToString()) };
        if (!string.IsNullOrEmpty(userType)) claims.Add(new Claim("u", userType));
        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim("r", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(expired ? -10 : -1),
            expires: expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateTokenWithInvalidSignature(Guid userId, string? userType = "cus", string? role = null)
    {
        using RSA otherKey = RSA.Create(2048);
        var key = new RsaSecurityKey(otherKey) { KeyId = "tampered-key-id" };
        var claims = new List<Claim> { new("sub", userId.ToString()) };
        if (!string.IsNullOrEmpty(userType)) claims.Add(new Claim("u", userType));
        if (!string.IsNullOrEmpty(role)) claims.Add(new Claim("r", role));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateUnsignedToken(Guid userId, string? userType = "cus", string? role = null)
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payloadObj = new Dictionary<string, object> { ["sub"] = userId.ToString() };
        if (!string.IsNullOrEmpty(userType)) payloadObj["u"] = userType;
        if (!string.IsNullOrEmpty(role)) payloadObj["r"] = role;
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _rsa.Dispose();
        }
    }

    private sealed class TestJwksConfigurationManager(RSA rsa, string keyId) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _config = CreateConfiguration(rsa, keyId);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_config);

        public void RequestRefresh() { }

        private static OpenIdConnectConfiguration CreateConfiguration(RSA rsa, string keyId)
        {
            var config = new OpenIdConnectConfiguration();
            config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
            return config;
        }
    }
}

/// <summary>
/// TimeProvider giả lập hỗ trợ kiểm soát thời gian trong các use case hết hạn (cửa sổ 30 ngày, 7 ngày).
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _defaultTime;
    private DateTimeOffset? _overrideTime;

    public TestTimeProvider(DateTimeOffset defaultTime)
    {
        _defaultTime = defaultTime;
    }

    public override DateTimeOffset GetUtcNow() => _overrideTime ?? _defaultTime;

    public void SetUtcNow(DateTimeOffset time) => _overrideTime = time;

    public void Reset() => _overrideTime = null;
}

/// <summary>
/// Client giả lập giao tiếp với RentalService cho phép nạp snapshot phục vụ kiểm thử tích hợp.
/// </summary>
public sealed class TestRentalServiceClient : IRentalServiceClient
{
    private readonly ConcurrentDictionary<(Guid OrderId, Guid ItemId), RentalItemReviewSnapshotDto> _snapshots = new();

    public void SetSnapshot(RentalItemReviewSnapshotDto snapshot)
    {
        _snapshots[(snapshot.OrderId, snapshot.ItemId)] = snapshot;
    }

    public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (_snapshots.TryGetValue((orderId, itemId), out var snapshot))
        {
            return Task.FromResult(Result.Success(snapshot));
        }

        return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(
            Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Mục thuê không tồn tại trong hệ thống RentalService.")));
    }

    public Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var items = _snapshots.Values.Take(limit).ToList();
        return Task.FromResult(Result.Success(new ReconciliationBatchDto(items, null, false)));
    }
}

/// <summary>
/// Client giả lập MediaService phục vụ kiểm thử tích hợp.
/// </summary>
public sealed class TestMediaServiceClient : IMediaServiceClient
{
    private readonly ConcurrentDictionary<Guid, Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto> _media = new();

    public void RegisterMedia(Guid mediaId, Guid uploadedBy, string status = "Ready", string contentType = "image/jpeg")
    {
        _media[mediaId] = new Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto(mediaId, uploadedBy, status, contentType);
    }

    public Task<Result<Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (_media.TryGetValue(mediaId, out var dto))
        {
            return Task.FromResult(Result.Success(dto));
        }

        return Task.FromResult(Result.Success(new Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto(mediaId, Guid.Empty, "Ready", "image/jpeg")));
    }

    public Task<Result<Dprental.ReviewService.Application.DTOs.Media.MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Success(new Dprental.ReviewService.Application.DTOs.Media.MediaAccessDto(
            mediaId,
            $"https://media.test/private/{mediaId}?sig=test-sig",
            DateTimeOffset.UtcNow.AddSeconds(60))));
    }
}
