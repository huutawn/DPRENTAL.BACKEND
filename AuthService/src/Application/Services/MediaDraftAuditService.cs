using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

public sealed class MediaDraftAuditService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IMediaServiceClient mediaServiceClient,
    IAuditLogRepository auditLogRepository,
    ICorrelationIdProvider correlationIdProvider,
    TimeProvider timeProvider,
    ILogger<MediaDraftAuditService> logger) : IMediaDraftAuditService
{
    private static readonly Action<ILogger, Guid, Guid, OrganizationStatus, Exception?> LogUnlinkMedia =
        LoggerMessage.Define<Guid, Guid, OrganizationStatus>(
            LogLevel.Warning,
            new EventId(1, nameof(AuditAndRepairDraftMediaAsync)),
            "Unlinking unauthorized or invalid media {MediaId} from Organization {OrgId} (Status: {Status})");

    public async Task<Result<MediaDraftAuditResult>> AuditAndRepairDraftMediaAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Organization> orgs = await organizationRepository.GetDraftOrRejectedWithMediaAsync(cancellationToken);
        int scannedCount = orgs.Count;
        int unlinkedCount = 0;
        List<Guid> repairedOrgIds = [];

        foreach (Organization org in orgs)
        {
            if (!org.BusinessLicenseDocumentMediaId.HasValue)
            {
                continue;
            }

            Guid mediaId = org.BusinessLicenseDocumentMediaId.Value;
            var activeOwner = await membershipRepository.GetActiveOwnerMembershipAsync(org.Id, cancellationToken);

            bool isAuthorized = false;
            if (activeOwner is not null)
            {
                var verifyResult = await mediaServiceClient.VerifyMediaAsync(mediaId, cancellationToken);
                if (verifyResult.IsSuccess)
                {
                    var docValidation = MediaVerificationRules.ValidateDocument(verifyResult.Value, activeOwner.UserId);
                    if (docValidation.IsSuccess)
                    {
                        isAuthorized = true;
                    }
                }
            }

            if (!isAuthorized)
            {
                LogUnlinkMedia(logger, mediaId, org.Id, org.Status, null);

                org.BusinessLicenseDocumentMediaId = null;
                await organizationRepository.UpdateAsync(org, cancellationToken);

                await auditLogRepository.AddAsync(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = Guid.Empty,
                    Target = org.Id.ToString(),
                    Action = "Organization_MediaAuditRepair",
                    Result = "Success",
                    Reason = $"Unlinked unauthorized media {mediaId} from organization {org.Id}.",
                    Status = org.Status,
                    CorrelationId = correlationIdProvider.GetCorrelationId(),
                    Timestamp = timeProvider.GetUtcNow()
                }, cancellationToken);

                unlinkedCount++;
                repairedOrgIds.Add(org.Id);
            }
        }

        return Result.Success(new MediaDraftAuditResult(scannedCount, unlinkedCount, repairedOrgIds));
    }
}
