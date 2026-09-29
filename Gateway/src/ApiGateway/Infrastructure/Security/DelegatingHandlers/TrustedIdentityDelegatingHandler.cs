using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Authentication;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Infrastructure.Security.DelegatingHandlers;

/// <summary>
/// Ocelot DelegatingHandler nhằm thực thi Trusted Identity Forwarding,
/// Header Sanitization, CorrelationId và Public Origin Forwarding ngay trước khi gửi request tới downstream service.
/// </summary>
public sealed class TrustedIdentityDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IIdentityExtractor identityExtractor;
    private readonly IIdentityContextAccessor identityContextAccessor;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;
    private readonly ApiKeyRoutePolicy routePolicy;

    /// <summary>
    /// Khởi tạo DelegatingHandler với các dịch vụ bảo mật và truy xuất ngữ cảnh HTTP.
    /// </summary>
    public TrustedIdentityDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        IIdentityExtractor identityExtractor,
        IIdentityContextAccessor identityContextAccessor,
        IOptions<GatewaySecurityOptions> securityOptions,
        ApiKeyRoutePolicy routePolicy)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        this.identityExtractor = identityExtractor ?? throw new ArgumentNullException(nameof(identityExtractor));
        this.identityContextAccessor = identityContextAccessor ?? throw new ArgumentNullException(nameof(identityContextAccessor));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        this.routePolicy = routePolicy ?? throw new ArgumentNullException(nameof(routePolicy));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var headers = request.Headers;

            // MediaService receives only the authenticated bearer token, never browser session cookies.
            bool isMediaRequest = httpContext.Request.Path.StartsWithSegments("/api/v1/media");
            if (isMediaRequest ||
                httpContext.Request.Path.StartsWithSegments("/media/swagger"))
            {
                headers.Remove("Cookie");
            }

            // 1. Header Sanitization: Loại bỏ toàn bộ tiêu đề nhạy cảm client tự gửi lên
            foreach (string untrustedHeader in GatewayHeaderConstants.UntrustedClientHeaders)
            {
                headers.Remove(untrustedHeader);
            }
            headers.Remove("X-Api-Key");
            if (routePolicy.RequiresApiKey(httpContext.Request))
            {
                headers.Remove("Authorization");
                if (httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKeys))
                {
                    headers.TryAddWithoutValidation("X-Api-Key", apiKeys.ToArray());
                }
            }

            // Gateway đã chấp nhận forwarded metadata chỉ từ trusted proxy đã cấu hình.
            // Ghi đè các giá trị downstream để upstream nhận đúng public origin, không phải địa chỉ nội bộ Docker.
            headers.Remove(GatewayHeaderConstants.ForwardedProto);
            headers.TryAddWithoutValidation(GatewayHeaderConstants.ForwardedProto, httpContext.Request.Scheme);
            headers.Remove(GatewayHeaderConstants.ForwardedHost);
            headers.TryAddWithoutValidation(GatewayHeaderConstants.ForwardedHost, httpContext.Request.Host.Value);

            // 2. CorrelationId Forwarding: Đảm bảo downstream luôn nhận CorrelationId an toàn
            string correlationId = httpContext.TraceIdentifier;
            if (string.IsNullOrWhiteSpace(correlationId) &&
                httpContext.Request.Headers.TryGetValue(GatewayHeaderConstants.CorrelationId, out var corrHeader))
            {
                correlationId = corrHeader.ToString();
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                headers.Remove(GatewayHeaderConstants.CorrelationId);
                headers.TryAddWithoutValidation(GatewayHeaderConstants.CorrelationId, correlationId);
            }

            // 3. Service Name Forwarding: Ghi đè tên Gateway xác thực, không cho phép client giả mạo
            string serviceName = securityOptions.Value.Identity.ServiceName;
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                headers.Remove(GatewayHeaderConstants.ServiceName);
                headers.TryAddWithoutValidation(GatewayHeaderConstants.ServiceName, serviceName);
            }

            // 4. Trusted Identity Forwarding:
            // Chỉ chuyển tiếp danh tính người dùng; service principal không được giả thành X-User-Id.
            if (httpContext.User?.Identity is not null && httpContext.User.Identity.IsAuthenticated &&
                !httpContext.User.HasClaim("u", "svc"))
            {
                IdentityContext identity = identityExtractor.Extract(httpContext.User, httpContext);
                if (identity.IsAuthenticated && !string.IsNullOrWhiteSpace(identity.UserId))
                {
                    // Chuyển tiếp UserId thật trích xuất từ claim "sub" của JWT
                    headers.Remove(GatewayHeaderConstants.UserId);
                    headers.TryAddWithoutValidation(GatewayHeaderConstants.UserId, identity.UserId);

                    // Chuyển tiếp PartyId nếu tồn tại trong token
                    headers.Remove(GatewayHeaderConstants.PartyId);
                    if (!string.IsNullOrWhiteSpace(identity.PartyId))
                    {
                        headers.TryAddWithoutValidation(GatewayHeaderConstants.PartyId, identity.PartyId);
                    }

                    // Chuyển tiếp các vai trò (Roles) hợp lệ đã xác thực
                    headers.Remove(GatewayHeaderConstants.Role);
                    foreach (string role in identity.Roles)
                    {
                        if (!string.IsNullOrWhiteSpace(role))
                        {
                            headers.TryAddWithoutValidation(GatewayHeaderConstants.Role, role);
                        }
                    }

                    // Cập nhật vào accessor để có thể truy xuất trong nội bộ Gateway nếu cần
                    identityContextAccessor.SetCurrent(identity);
                }

                // Chuyển đổi Cookie token sang Authorization Bearer cho downstream nếu client chỉ gửi Cookie
                if (headers.Authorization is null &&
                    httpContext.Request.Cookies.TryGetValue("accessToken", out string? cookieToken) &&
                    !string.IsNullOrWhiteSpace(cookieToken))
                {
                    headers.TryAddWithoutValidation("Authorization", $"Bearer {cookieToken}");
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
