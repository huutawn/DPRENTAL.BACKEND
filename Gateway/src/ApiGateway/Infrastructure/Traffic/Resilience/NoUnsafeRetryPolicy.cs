using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Domain.Traffic;
using Microsoft.Extensions.Logging;

namespace Dprental.Gateway.Infrastructure.Traffic.Resilience;

/// <summary>
/// Thực thi chính sách an toàn thử lại (Retry Safety):
/// Ngăn chặn tuyệt đối hành vi tự động thử lại (Retry) đối với các lệnh nghiệp vụ không an toàn (Unsafe / Non-Idempotent Commands)
/// như POST, PATCH khi gặp lỗi timeout hoặc gián đoạn kết nối, nhằm tránh nguy cơ tạo trùng lặp dữ liệu (Duplicate Business Effect).
/// </summary>
public sealed class NoUnsafeRetryPolicy
{
    private static readonly Action<ILogger, string, Exception?> LogUnsafeRetryBlocked = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(1, "UnsafeRetryBlocked"),
        "Cơ chế thử lại đã bị vô hiệu hóa đối với phương thức không an toàn {HttpMethod} nhằm bảo vệ tính toàn vẹn dữ liệu nghiệp vụ.");

    private readonly ILogger<NoUnsafeRetryPolicy> logger;

    /// <summary>
    /// Khởi tạo chính sách kiểm soát thử lại.
    /// </summary>
    /// <param name="logger">Logger ghi log chẩn đoán.</param>
    public NoUnsafeRetryPolicy(ILogger<NoUnsafeRetryPolicy> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Kiểm tra xem một phương thức HTTP có được phép thử lại (Retry) hay không.
    /// </summary>
    /// <param name="httpMethod">Phương thức HTTP (GET, POST, PUT, DELETE, PATCH, v.v.).</param>
    /// <param name="options">Cấu hình khả năng phục hồi của Gateway.</param>
    /// <returns>True nếu an toàn để thử lại; False nếu bị cấm thử lại.</returns>
    public bool IsRetryAllowed(string httpMethod, GatewayResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpMethod);
        ArgumentNullException.ThrowIfNull(options);

        // Kiểm tra xem phương thức có nằm trong danh mục Unsafe (POST, PATCH, CONNECT) hay không
        if (ResilienceConstants.UnsafeHttpMethods.Contains(httpMethod))
        {
            if (!options.AllowUnsafeCommandRetries)
            {
                LogUnsafeRetryBlocked(logger, httpMethod, null);
                return false;
            }
        }

        return true;
    }
}
