using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default);

    Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(
        Guid organizationId,
        PageRequest request,
        string? action = null,
        OrganizationStatus? status = null,
        string? result = null,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
