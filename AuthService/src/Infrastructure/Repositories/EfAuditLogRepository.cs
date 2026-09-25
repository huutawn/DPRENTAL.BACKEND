using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Infrastructure.Data;

namespace Dprental.AuthService.Infrastructure.Repositories;

public sealed class EfAuditLogRepository(AuthDbContext dbContext) : IAuditLogRepository
{
    public async Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditLog);

        if (auditLog.Timestamp == default)
        {
            auditLog.Timestamp = DateTimeOffset.UtcNow;
        }

        dbContext.AuditLogs.Add(auditLog);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
