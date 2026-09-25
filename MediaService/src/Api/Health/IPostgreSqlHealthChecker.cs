using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.MediaService.Api.Health;

/// <summary>
/// Giao diện kiểm tra kết nối đến cơ sở dữ liệu PostgreSQL.
/// </summary>
public interface IPostgreSqlHealthChecker
{
    /// <summary>
    /// Thực hiện kiểm tra kết nối PostgreSQL.
    /// </summary>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Kết quả kiểm tra sức khỏe.</returns>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}
