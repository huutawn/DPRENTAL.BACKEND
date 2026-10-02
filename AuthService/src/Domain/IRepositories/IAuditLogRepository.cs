using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default);

    Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(
        Guid organizationId,
        PageRequest request,
        CancellationToken cancellationToken = default);
}
