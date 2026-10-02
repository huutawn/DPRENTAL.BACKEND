using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class GatewayRateLimiterUnitTests
{
    private readonly IdentityExtractor identityExtractor = new();

    private GatewayRateLimiter CreateLimiter(GatewayRateLimitingOptions? rateLimitingOptions = null)
    {
        GatewaySecurityOptions securityOptions = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = rateLimitingOptions ?? new GatewayRateLimitingOptions()
        };

        return new GatewayRateLimiter(Options.Create(securityOptions), identityExtractor);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void ResolvePartitionKeyReadMethodsClassifiedAsReadGroup(string method)
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = "/api/v1/catalog/items";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.ReadGroup, key.RequestGroup);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal("192.0.2.1", key.IdentityValue);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PURGE")]
    public void ResolvePartitionKeyWriteMethodsClassifiedAsWriteGroup(string method)
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = "/api/v1/catalog/items";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.WriteGroup, key.RequestGroup);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal("192.0.2.1", key.IdentityValue);
    }

    [Fact]
    public void ResolvePartitionKeyAuthenticatedUserUsesUserIdPartition()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/auth/me";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");

        ClaimsIdentity identity = new([new Claim("sub", "user-uuid-123")], "Bearer");
        context.User = new ClaimsPrincipal(identity);

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.UserType, key.IdentityType);
        Assert.Equal("user-uuid-123", key.IdentityValue);
        Assert.Equal(RateLimitPartitionKey.ReadGroup, key.RequestGroup);
    }

    [Fact]
    public void ResolvePartitionKeyAuthenticatedUserIgnoresUntrustedUserIdHeader()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/orders";
        context.Request.Headers["X-User-Id"] = "attacker-user-id";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.50");

        ClaimsIdentity identity = new([new Claim("sub", "real-user-id")], "Bearer");
        context.User = new ClaimsPrincipal(identity);

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.UserType, key.IdentityType);
        Assert.Equal("real-user-id", key.IdentityValue);
        Assert.Equal(RateLimitPartitionKey.WriteGroup, key.RequestGroup);
    }

    [Fact]
    public void ResolvePartitionKeyApiKeyWithoutUserIdFallsBackToIpPartition()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/media/upload";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.88");

        // API Key tạo principal với claim u=svc nhưng không có claim định danh user (sub/userId)
        ClaimsIdentity identity = new([new Claim("u", "svc")], "Bearer");
        context.User = new ClaimsPrincipal(identity);

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal("192.0.2.88", key.IdentityValue);
        Assert.Equal(RateLimitPartitionKey.WriteGroup, key.RequestGroup);
    }

    [Fact]
    public void ResolvePartitionKeyUnauthenticatedUserUsesIpPartition()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/catalog/categories";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.99");
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated = false

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal("203.0.113.99", key.IdentityValue);
        Assert.Equal(RateLimitPartitionKey.ReadGroup, key.RequestGroup);
    }

    [Fact]
    public void ResolvePartitionKeyIpv4MappedIpv6NormalizedToIpv4()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/catalog";
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.45");

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal("192.0.2.45", key.IdentityValue);
    }

    [Fact]
    public void ResolvePartitionKeyNullRemoteIpUsesUnknownFallback()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/catalog";
        context.Connection.RemoteIpAddress = null;

        bool hasKey = limiter.TryResolvePartitionKey(context, out RateLimitPartitionKey key);

        Assert.True(hasKey);
        Assert.Equal(RateLimitPartitionKey.IpType, key.IdentityType);
        Assert.Equal(RateLimitPartitionKey.UnknownIp, key.IdentityValue);
    }

    [Fact]
    public void TryResolvePartitionKeyOptionsMethodReturnsFalseExempt()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "OPTIONS";
        context.Request.Path = "/api/v1/catalog";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");

        bool hasKey = limiter.TryResolvePartitionKey(context, out _);

        Assert.False(hasKey);
        Assert.True(GatewayRateLimiter.IsExempt(context));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/health")]
    public void TryResolvePartitionKeyHealthCheckPathsReturnsFalseExempt(string healthPath)
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = healthPath;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");

        bool hasKey = limiter.TryResolvePartitionKey(context, out _);

        Assert.False(hasKey);
        Assert.True(GatewayRateLimiter.IsExempt(context));
    }

    [Fact]
    public void CheckRateLimitExemptRequestAlwaysReturnsAllowed()
    {
        using GatewayRateLimiter limiter = CreateLimiter();
        DefaultHttpContext context = new();
        context.Request.Method = "OPTIONS";
        context.Request.Path = "/api/v1/catalog";

        GatewayRateLimitResult result = limiter.CheckRateLimit(context);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void CheckRateLimitWhenLimitExceededReturnsBlockedWithRetryAfter()
    {
        // Giới hạn 2 request trong cửa sổ 60s
        GatewayRateLimitingOptions options = new()
        {
            WindowSeconds = 60,
            SegmentsPerWindow = 6,
            UserReadLimit = 2
        };

        using GatewayRateLimiter limiter = CreateLimiter(options);

        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/catalog/items";
        ClaimsIdentity identity = new([new Claim("sub", "rate-limit-test-user")], "Bearer");
        context.User = new ClaimsPrincipal(identity);

        // Request 1: OK
        GatewayRateLimitResult result1 = limiter.CheckRateLimit(context);
        Assert.True(result1.IsAllowed);

        // Request 2: OK
        GatewayRateLimitResult result2 = limiter.CheckRateLimit(context);
        Assert.True(result2.IsAllowed);

        // Request 3: Vượt quota (Blocked)
        GatewayRateLimitResult result3 = limiter.CheckRateLimit(context);
        Assert.False(result3.IsAllowed);
        Assert.True(result3.RetryAfterSeconds >= 1);
        Assert.Equal(RateLimitPartitionKey.ReadGroup, result3.RequestGroup);
        Assert.Equal(RateLimitPartitionKey.UserType, result3.PartitionType);
    }

    [Fact]
    public void MetricsRecordedWithoutSensitiveLabels()
    {
        List<Measurement<long>> recordedMeasurements = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayRateLimitingMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            recordedMeasurements.Add(new Measurement<long>(measurement, tags));
        });

        listener.Start();

        GatewayRateLimitingMetrics.RecordBlockedRequest("read", "user");

        Assert.NotEmpty(recordedMeasurements);
        var measurement = recordedMeasurements.Last();
        Assert.Equal(1, measurement.Value);

        var tagsDict = measurement.Tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.True(tagsDict.ContainsKey("request_group"));
        Assert.Equal("read", tagsDict["request_group"]);
        Assert.True(tagsDict.ContainsKey("partition_type"));
        Assert.Equal("user", tagsDict["partition_type"]);

        // Đảm bảo tuyệt đối không có nhãn chứa token, user ID hoặc IP
        Assert.False(tagsDict.ContainsKey("user_id"));
        Assert.False(tagsDict.ContainsKey("ip"));
        Assert.False(tagsDict.ContainsKey("token"));
        Assert.False(tagsDict.ContainsKey("key"));
    }
}
