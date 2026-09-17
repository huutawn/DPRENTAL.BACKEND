using System.Net;
using Dprental.Gateway.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Dprental.Gateway.Infrastructure.Security.ForwardedHeaders;

/// <summary>
/// Cấu hình xử lý tiêu đề ủy quyền (Forwarded Headers) cho ASP.NET Core
/// dựa trên cấu hình mạng tin cậy (Known Networks / Known Proxies).
/// </summary>
public static class ForwardedHeadersConfiguration
{
    /// <summary>
    /// Áp dụng cấu hình ForwardedHeadersOptions từ cấu hình bảo mật Gateway.
    /// </summary>
    /// <param name="options">Đối tượng ForwardedHeadersOptions của ASP.NET Core.</param>
    /// <param name="securityOptions">Cấu hình bảo mật Gateway.</param>
    public static void Configure(ForwardedHeadersOptions options, GatewaySecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(securityOptions);

        GatewayForwardedHeadersOptions forwardedOptions = securityOptions.ForwardedHeaders;
        if (!forwardedOptions.Enabled)
        {
            return;
        }

        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                                   Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
                                   Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;

        options.ForwardLimit = forwardedOptions.ForwardLimit;

        // Cấu hình danh sách dải mạng IP tin cậy (CIDR)
        foreach (string networkCidr in forwardedOptions.KnownNetworks)
        {
            if (!string.IsNullOrWhiteSpace(networkCidr))
            {
                // Hỗ trợ phân tích cú pháp CIDR chuẩn qua IPNetwork của .NET
                string[] parts = networkCidr.Split('/');
                if (parts.Length == 2 &&
                    IPAddress.TryParse(parts[0], out IPAddress? ip) &&
                    int.TryParse(parts[1], out int prefixLength))
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(ip, prefixLength));
            }
        }

        // Cấu hình danh sách địa chỉ IP proxy tin cậy
        foreach (string proxyIp in forwardedOptions.KnownProxies)
        {
            if (!string.IsNullOrWhiteSpace(proxyIp) && IPAddress.TryParse(proxyIp, out IPAddress? address))
            {
                options.KnownProxies.Add(address);
            }
        }
    }
}
