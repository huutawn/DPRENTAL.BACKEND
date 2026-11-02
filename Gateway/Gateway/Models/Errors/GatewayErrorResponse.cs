namespace Dprental.Gateway.Models.Errors;

/// <summary>
/// Cấu trúc JSON thống nhất cho lỗi do chính Gateway sinh ra.
/// Code và Message tuân theo hợp đồng lỗi ổn định; TraceId giúp đối chiếu log của request.
/// </summary>
internal sealed record GatewayErrorResponse(string Code, string Message, string TraceId);
