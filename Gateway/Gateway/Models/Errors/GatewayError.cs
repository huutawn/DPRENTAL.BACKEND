namespace Dprental.Gateway.Models.Errors;

/// <summary>Danh mục mã lỗi ổn định trả cho client từ các lớp xử lý HTTP do Gateway sở hữu.</summary>
internal sealed record GatewayError(string Code, string Message)
{
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

    /// <summary>Lỗi khi số lượng request vượt quá giới hạn tần suất cho phép (Rate Limit Exceeded).</summary>
    internal static readonly GatewayError RateLimitExceeded = new(
        "Gateway.RateLimitExceeded",
        "Too many requests. Please try again later.");

}
