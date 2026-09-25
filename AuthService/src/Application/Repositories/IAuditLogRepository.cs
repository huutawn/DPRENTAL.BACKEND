using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Repositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default);
}
