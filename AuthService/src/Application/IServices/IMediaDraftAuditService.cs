using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.IServices;

public sealed record MediaDraftAuditResult(
    int ScannedCount,
    int UnlinkedCount,
    IReadOnlyList<Guid> RepairedOrganizationIds);

public interface IMediaDraftAuditService
{
    Task<Result<MediaDraftAuditResult>> AuditAndRepairDraftMediaAsync(CancellationToken cancellationToken = default);
}
