using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Domain.Security;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Dprental.Gateway.Infrastructure.Security.Transforms;

/// <summary>
/// Cung cấp bộ chuyển đổi request (YARP Request Transform) nhằm thực thi Trusted Identity Forwarding
/// và Header Sanitization ngay tại ranh giới chuyển tiếp request tới downstream microservices.
/// </summary>
public sealed class TrustedIdentityTransformProvider : ITransformProvider
{
    private readonly IIdentityExtractor identityExtractor;
    private readonly IIdentityContextAccessor identityContextAccessor;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;

    /// <summary>
    /// Khởi tạo provider chuyển đổi danh tính đáng tin cậy.
    /// </summary>
    /// <param name="identityExtractor">Bộ trích xuất danh tính từ token.</param>
    /// <param name="identityContextAccessor">Bộ lưu trữ ngữ cảnh danh tính của request.</param>
    /// <param name="securityOptions">Cấu hình bảo mật Gateway.</param>
    public TrustedIdentityTransformProvider(
        IIdentityExtractor identityExtractor,
        IIdentityContextAccessor identityContextAccessor,
        IOptions<GatewaySecurityOptions> securityOptions)
    {
        this.identityExtractor = identityExtractor ?? throw new ArgumentNullException(nameof(identityExtractor));
        this.identityContextAccessor = identityContextAccessor ?? throw new ArgumentNullException(nameof(identityContextAccessor));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
    }

    /// <inheritdoc />
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        // Tuyến đường không yêu cầu thẩm định thêm quy tắc cấu hình route tĩnh
    }

    /// <inheritdoc />
    public void ValidateCluster(TransformClusterValidationContext context)
    {
        // Cluster không yêu cầu thẩm định thêm quy tắc cấu hình cluster tĩnh
    }

    /// <inheritdoc />
    public void Apply(TransformBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddRequestTransform(transformContext =>
        {
            var proxyHeaders = transformContext.ProxyRequest.Headers;
            var httpContext = transformContext.HttpContext;

            // 1. Header Sanitization: Loại bỏ toàn bộ tiêu đề nhạy cảm client tự gửi lên
            foreach (string untrustedHeader in GatewayHeaderConstants.UntrustedClientHeaders)
            {
                proxyHeaders.Remove(untrustedHeader);
            }

            // 2. CorrelationId Forwarding: Đảm bảo downstream luôn nhận CorrelationId an toàn
            string correlationId = httpContext.TraceIdentifier;
            if (string.IsNullOrWhiteSpace(correlationId) &&
                httpContext.Request.Headers.TryGetValue(GatewayHeaderConstants.CorrelationId, out var corrHeader))
            {
                correlationId = corrHeader.ToString();
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                proxyHeaders.Remove(GatewayHeaderConstants.CorrelationId);
                proxyHeaders.TryAddWithoutValidation(GatewayHeaderConstants.CorrelationId, correlationId);
            }

            // 3. Service Name Forwarding: Ghi đè tên Gateway xác thực, không cho phép client giả mạo
            string serviceName = securityOptions.Value.Identity.ServiceName;
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                proxyHeaders.Remove(GatewayHeaderConstants.ServiceName);
                proxyHeaders.TryAddWithoutValidation(GatewayHeaderConstants.ServiceName, serviceName);
            }

            // 4. Trusted Identity Forwarding:
            // Chỉ trích xuất và chuyển tiếp danh tính khi JWT đã được xác thực thành công qua middleware authentication
            if (httpContext.User?.Identity is not null && httpContext.User.Identity.IsAuthenticated)
            {
                IdentityContext identity = identityExtractor.Extract(httpContext.User, httpContext);
                if (identity.IsAuthenticated && !string.IsNullOrWhiteSpace(identity.UserId))
                {
                    // Chuyển tiếp UserId thật trích xuất từ claim "sub" của JWT
                    proxyHeaders.TryAddWithoutValidation(GatewayHeaderConstants.UserId, identity.UserId);

                    // Chuyển tiếp PartyId nếu tồn tại trong token
                    if (!string.IsNullOrWhiteSpace(identity.PartyId))
                    {
                        proxyHeaders.TryAddWithoutValidation(GatewayHeaderConstants.PartyId, identity.PartyId);
                    }

                    // Chuyển tiếp các vai trò (Roles) hợp lệ đã xác thực
                    foreach (string role in identity.Roles)
                    {
                        if (!string.IsNullOrWhiteSpace(role))
                        {
                            proxyHeaders.TryAddWithoutValidation(GatewayHeaderConstants.Role, role);
                        }
                    }

                    // Cập nhật vào accessor để có thể truy xuất trong nội bộ Gateway nếu cần
                    identityContextAccessor.SetCurrent(identity);
                }

                // Chuyển đổi Cookie token sang Authorization Bearer cho downstream nếu client chỉ gửi Cookie
                if (proxyHeaders.Authorization is null &&
                    httpContext.Request.Cookies.TryGetValue("accessToken", out string? cookieToken) &&
                    !string.IsNullOrWhiteSpace(cookieToken))
                {
                    proxyHeaders.TryAddWithoutValidation("Authorization", $"Bearer {cookieToken}");
                }
            }

            return ValueTask.CompletedTask;
        });
    }
}
