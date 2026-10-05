using Microsoft.EntityFrameworkCore;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Common.Utils;
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
        string? action = null,
        OrganizationStatus? status = null,
        string? result = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string target = organizationId.ToString();

        IQueryable<AuditLog> query = dbContext.AuditLogs
            .AsNoTracking()
            .Where(l => l.Target == target && l.Action.StartsWith("Organization_"));

        string? actionFilter = StringUtility.NullIfBlank(action);
        string? resultFilter = StringUtility.NullIfBlank(result);
        string? reasonFilter = StringUtility.NullIfBlank(reason);
        string? searchTerm = request.GetSearchTerm();

#pragma warning disable CA1304, CA1311, CA1862
        if (actionFilter is not null)
        {
            string lowerAction = actionFilter.ToLowerInvariant();
            query = query.Where(l => l.Action != null && l.Action.ToLower() == lowerAction);
        }

        if (status.HasValue)
        {
            OrganizationStatus statusValue = status.Value;
            query = query.Where(l => l.Status == statusValue);
        }

        if (resultFilter is not null)
        {
            string lowerResult = resultFilter.ToLowerInvariant();
            query = query.Where(l => l.Result != null && l.Result.ToLower() == lowerResult);
        }

        if (reasonFilter is not null)
        {
            string lowerReason = reasonFilter.ToLowerInvariant();
            query = query.Where(l => l.Reason != null && l.Reason.ToLower().Contains(lowerReason));
        }

        if (searchTerm is not null)
        {
            List<OrganizationStatus?> matchingStatuses = Enum.GetValues<OrganizationStatus>()
                .Where(s => s.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Select(s => (OrganizationStatus?)s)
                .ToList();

            if (matchingStatuses.Count > 0)
            {
                query = query.Where(l =>
                    (l.Action != null && l.Action.ToLower().Contains(searchTerm)) ||
                    matchingStatuses.Contains(l.Status) ||
                    (l.Result != null && l.Result.ToLower().Contains(searchTerm)) ||
                    (l.Reason != null && l.Reason.ToLower().Contains(searchTerm)));
            }
            else
            {
                query = query.Where(l =>
                    (l.Action != null && l.Action.ToLower().Contains(searchTerm)) ||
                    (l.Result != null && l.Result.ToLower().Contains(searchTerm)) ||
                    (l.Reason != null && l.Reason.ToLower().Contains(searchTerm)));
            }
        }
#pragma warning restore CA1304, CA1311, CA1862
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
