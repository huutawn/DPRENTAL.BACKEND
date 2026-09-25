using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Dprental.MediaService.Api.Health;

/// <summary>
/// Triển khai mặc định kiểm tra kết nối PostgreSQL thực tế bằng NpgsqlConnection.
/// </summary>
public sealed class DefaultPostgreSqlHealthChecker : IPostgreSqlHealthChecker
{
    private readonly string _connectionString;

    public DefaultPostgreSqlHealthChecker(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu chuỗi kết nối 'Postgres' trong cấu hình.");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(timeoutCts.Token);

            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(timeoutCts.Token);

            return HealthCheckResult.Healthy("Kết nối PostgreSQL thành công.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Không thể kết nối đến PostgreSQL: {ex.Message}", ex);
        }
    }
}
