using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dprental.Gateway.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
namespace Dprental.Gateway.Security;

/// <summary>
/// Ocelot DelegatingHandler nhằm thực thi Trusted Identity Forwarding,
/// Header Sanitization, CorrelationId và Public Origin Forwarding ngay trước khi gửi request tới downstream service.
/// </summary>
public sealed class TrustedIdentityDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IdentityExtractor identityExtractor;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;
    private readonly ApiKeyRoutePolicy? routePolicy;
    private readonly string _internalApiKey;

    /// <summary>
    /// Khởi tạo DelegatingHandler với các dịch vụ bảo mật và truy xuất ngữ cảnh HTTP.
    /// </summary>
    public TrustedIdentityDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        IdentityExtractor identityExtractor,
        IOptions<GatewaySecurityOptions> securityOptions,
        IConfiguration configuration,
        ApiKeyRoutePolicy? routePolicy = null)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        this.identityExtractor = identityExtractor ?? throw new ArgumentNullException(nameof(identityExtractor));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        this.routePolicy = routePolicy;
        _internalApiKey = configuration["InternalAuthentication:ApiKey"]
            ?? configuration["INTERNAL_API_KEY"]
            ?? string.Empty;
    }

    public TrustedIdentityDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        IdentityExtractor identityExtractor,
        IOptions<GatewaySecurityOptions> securityOptions,
        ApiKeyRoutePolicy routePolicy)
        : this(httpContextAccessor, identityExtractor, securityOptions, new ConfigurationBuilder().Build(), routePolicy)
    {
    }
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var headers = request.Headers;

            // Các service tự xác thực JWT chỉ nhận bearer token, không nhận browser session cookies
            bool isMediaRequest = httpContext.Request.Path.StartsWithSegments("/api/v1/media");
            bool isInventoryRequest = httpContext.Request.Path.StartsWithSegments("/api/v1/inventory");
            bool isCatalogRequest = httpContext.Request.Path.StartsWithSegments("/api/v1/catalog");
            if (isMediaRequest ||
                isInventoryRequest || isCatalogRequest ||
                httpContext.Request.Path.StartsWithSegments("/catalog/swagger") ||
                httpContext.Request.Path.StartsWithSegments("/media/swagger") ||
                httpContext.Request.Path.StartsWithSegments("/inventory/swagger"))
            {
                headers.Remove("Cookie");
            }

            // 1. Header Sanitization: Loại bỏ toàn bộ tiêu đề nhạy cảm client tự gửi lên
            foreach (string untrustedHeader in GatewayHeaderConstants.UntrustedClientHeaders)
            {
                headers.Remove(untrustedHeader);
            }
            headers.Remove("X-Api-Key");
            if (IsInternalBackend(request.RequestUri) && !string.IsNullOrWhiteSpace(_internalApiKey))
            {
                headers.TryAddWithoutValidation("X-Api-Key", _internalApiKey);
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

            // Chuyển đổi Cookie token sang Authorization Bearer cho downstream nếu client chỉ gửi Cookie
            if (headers.Authorization is null &&
                httpContext.Request.Cookies.TryGetValue("accessToken", out string? cookieToken) &&
                !string.IsNullOrWhiteSpace(cookieToken))
            {
                headers.TryAddWithoutValidation("Authorization", $"Bearer {cookieToken}");
            }

            // 4. Trusted Identity Forwarding:
            // Chỉ chuyển tiếp danh tính người dùng cho các service tin cậy header;
            // InventoryService và CatalogService tự xác thực JWT.
            if (!isInventoryRequest && !isCatalogRequest &&
                httpContext.User?.Identity is not null && httpContext.User.Identity.IsAuthenticated &&
                !httpContext.User.HasClaim("u", "svc"))
            {
                IdentityContext identity = identityExtractor.Extract(httpContext.User);
                if (identity.IsAuthenticated && !string.IsNullOrWhiteSpace(identity.UserId))
                {
                    headers.Remove(GatewayHeaderConstants.UserId);
                    headers.TryAddWithoutValidation(GatewayHeaderConstants.UserId, identity.UserId);

                    headers.Remove(GatewayHeaderConstants.PartyId);
                    if (!string.IsNullOrWhiteSpace(identity.PartyId))
                    {
                        headers.TryAddWithoutValidation(GatewayHeaderConstants.PartyId, identity.PartyId);
                    }

                    headers.Remove(GatewayHeaderConstants.Role);
                    foreach (string role in identity.Roles)
                    {
                        if (!string.IsNullOrWhiteSpace(role))
                        {
                            headers.TryAddWithoutValidation(GatewayHeaderConstants.Role, role);
                        }
                    }
                }
            }
            else if (isInventoryRequest || isCatalogRequest)
            {
                // Service tự xác thực JWT không nhận identity headers từ Gateway
                headers.Remove(GatewayHeaderConstants.UserId);
                headers.Remove(GatewayHeaderConstants.PartyId);
                headers.Remove(GatewayHeaderConstants.Role);
                headers.Remove(GatewayHeaderConstants.OrganizationId);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsInternalBackend(Uri? uri)
    {
        if (uri is null) return false;
        string host = uri.Host;
        return host is "auth-service" or "catalog-service" or "inventory-service" or "media-service" or "rental-service" or "payment-service" or "recommendation-service"
            || host is "localhost" or "127.0.0.1" or "downstream";
    }
}
