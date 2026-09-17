namespace Dprental.Gateway.Errors;

/// <summary>Stable, client-facing errors emitted by Gateway-owned HTTP boundaries.</summary>
internal sealed record GatewayError(string Code, string Message)
{
    internal static readonly GatewayError AuthenticationRequired = new(
        "Gateway.Authentication.Required",
        "Authentication is required to access this resource.");

    internal static readonly GatewayError InvalidAccessToken = new(
        "Gateway.Authentication.InvalidToken",
        "The access token is invalid or expired.");

    internal static readonly GatewayError AccessForbidden = new(
        "Gateway.Authorization.Forbidden",
        "You do not have permission to access this resource.");

    internal static readonly GatewayError UpstreamUnavailable = new(
        "Gateway.Upstream.Unavailable",
        "The requested service is temporarily unavailable.");

    internal static readonly GatewayError UpstreamTimeout = new(
        "Gateway.Upstream.Timeout",
        "The requested service did not respond in time.");

    internal static readonly GatewayError UnexpectedFailure = new(
        "Gateway.UnexpectedFailure",
        "An unexpected gateway error occurred.");

    /// <summary>Lỗi khi nguồn gốc request (Origin) không nằm trong danh sách whitelist của CORS policy.</summary>
    internal static readonly GatewayError CorsOriginDenied = new(
        "Gateway.Security.CorsOriginDenied",
        "The request origin is not allowed by CORS policy.");

    /// <summary>Lỗi khi kích thước request body vượt quá giới hạn cho phép.</summary>
    internal static readonly GatewayError PayloadTooLarge = new(
        "Gateway.Request.PayloadTooLarge",
        "The request payload exceeds the maximum allowed size.");

    /// <summary>Lỗi khi client gửi vượt quá số lượng request cho phép trong khung thời gian quy định (Rate Limit Exceeded).</summary>
    internal static readonly GatewayError RateLimitExceeded = new(
        "Gateway.Traffic.RateLimitExceeded",
        "Too many requests. Please try again later.");
}
