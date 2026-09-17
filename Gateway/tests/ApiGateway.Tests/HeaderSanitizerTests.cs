using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ làm sạch tiêu đề HeaderSanitizer.
/// Đảm bảo các tiêu đề nhạy cảm client tự ý gửi lên bị loại bỏ hoàn toàn.
/// </summary>
public sealed class HeaderSanitizerTests
{
    private readonly HeaderSanitizer sanitizer = new(NullLogger<HeaderSanitizer>.Instance);

    [Fact]
    public void ContainsUntrustedHeadersReturnsTrueWhenUntrustedHeadersPresent()
    {
        HeaderDictionary headers = new()
        {
            [GatewayHeaderConstants.UserId] = "spoofed-user",
            ["Authorization"] = "Bearer token",
            ["Content-Type"] = "application/json"
        };

        Assert.True(sanitizer.ContainsUntrustedHeaders(headers));
    }

    [Fact]
    public void ContainsUntrustedHeadersReturnsFalseWhenOnlyLegitimateHeadersPresent()
    {
        HeaderDictionary headers = new()
        {
            ["Authorization"] = "Bearer token",
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json"
        };

        Assert.False(sanitizer.ContainsUntrustedHeaders(headers));
    }

    [Fact]
    public void SanitizeInboundHeadersRemovesAllUntrustedHeadersAndRetainsLegitimateHeaders()
    {
        HeaderDictionary headers = new()
        {
            [GatewayHeaderConstants.UserId] = "fake-user",
            [GatewayHeaderConstants.PartyId] = "fake-party",
            [GatewayHeaderConstants.Role] = "Admin",
            [GatewayHeaderConstants.ServiceName] = "FakeService",
            ["X-Roles"] = "Manager",
            ["X-Permissions"] = "orders:read",
            ["Authorization"] = "Bearer real-token",
            ["Content-Type"] = "application/json"
        };

        IReadOnlyList<string> removed = sanitizer.SanitizeInboundHeaders(headers);

        Assert.Contains(GatewayHeaderConstants.UserId, removed);
        Assert.Contains(GatewayHeaderConstants.PartyId, removed);
        Assert.Contains(GatewayHeaderConstants.Role, removed);
        Assert.Contains(GatewayHeaderConstants.ServiceName, removed);

        // Header nhạy cảm bị loại bỏ khỏi headers
        Assert.False(headers.ContainsKey(GatewayHeaderConstants.UserId));
        Assert.False(headers.ContainsKey(GatewayHeaderConstants.PartyId));
        Assert.False(headers.ContainsKey(GatewayHeaderConstants.Role));
        Assert.False(headers.ContainsKey(GatewayHeaderConstants.ServiceName));

        // Header hợp lệ vẫn được giữ nguyên
        Assert.True(headers.ContainsKey("Authorization"));
        Assert.True(headers.ContainsKey("Content-Type"));
    }
}
