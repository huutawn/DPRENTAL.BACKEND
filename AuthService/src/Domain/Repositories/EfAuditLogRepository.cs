using Microsoft.EntityFrameworkCore;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Domain.Repositories;

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

    public async Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(
        Guid organizationId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string target = organizationId.ToString();

        IQueryable<AuditLog> query = dbContext.AuditLogs
            .AsNoTracking()
            .Where(l => l.Target == target && l.Action.StartsWith("Organization_"));

        IOrderedQueryable<AuditLog> ordered = query
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id);

        int totalCount = await ordered.CountAsync(cancellationToken);

        int skip = (request.Page - 1) * request.PageSize;
        List<AuditLog> items = await ordered
            .Skip(skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLog>(items, request.Page, request.PageSize, totalCount);
    }
}
