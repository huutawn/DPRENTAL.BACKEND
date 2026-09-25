using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.MediaService.Api.Health;

/// <summary>
/// Health check phục vụ probe readiness của PostgreSQL.
/// </summary>
public sealed class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly IPostgreSqlHealthChecker _checker;

    public PostgreSqlHealthCheck(IPostgreSqlHealthChecker checker)
    {
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return _checker.CheckHealthAsync(cancellationToken);
    }
}
