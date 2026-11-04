using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.NotificationService.Api.HealthChecks;

public sealed class DatabaseMigrationHealthCheck : IHealthCheck
{
    private readonly NotificationServiceDbContext _dbContext;

    public DatabaseMigrationHealthCheck(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_dbContext.Database.IsRelational())
            {
                return HealthCheckResult.Healthy("Database is not relational (InMemory provider in use).");
            }

            var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pendingMigrations.ToList();
            if (pendingList.Count > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Có {pendingList.Count} migration chưa được áp dụng: {string.Join(", ", pendingList)}");
            }

            return HealthCheckResult.Healthy("Database migrations up to date.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Lỗi kiểm tra pending migrations: {ex.Message}");
        }
    }
}
