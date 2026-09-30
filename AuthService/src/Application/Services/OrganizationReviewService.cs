using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Chỉ Pending mới được duyệt/từ chối. Trạng thái được kiểm tra sau khi khóa row, nên hai admin thao tác
/// cùng lúc thì người đến sau nhận <see cref="AuthErrors.Organization.NotPendingReview"/> thay vì ghi đè kết quả.
/// </summary>
public sealed class OrganizationReviewService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IRoleRepository roleRepository,
    IAuditLogRepository auditLogRepository,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IMediaUrlResolver mediaUrlResolver) : IOrganizationReviewService
{
    public Task<Result<OrganizationResponse>> ApproveAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default) =>
        ReviewAsync(actorUserId, organizationId, OrganizationStatus.Active, rejectionReason: null, "Organization_Approve", cancellationToken);

    public Task<Result<OrganizationResponse>> RejectAsync(
        Guid actorUserId,
        Guid organizationId,
        RejectOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Task.FromResult(Result.Failure<OrganizationResponse>(validationError));
        }

        return ReviewAsync(actorUserId, organizationId, OrganizationStatus.Rejected, request.Reason.Trim(), "Organization_Reject", cancellationToken);
    }

    private async Task<Result<OrganizationResponse>> ReviewAsync(
        Guid actorUserId,
        Guid organizationId,
        OrganizationStatus decision,
        string? rejectionReason,
        string auditAction,
        CancellationToken cancellationToken)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!await IsPlatformAdminAsync(actorUserId, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.PlatformAdminRequired);
        }

        Organization? before = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (before is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }
        if (await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken) is not null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SelfReviewNotAllowed);
        }
        if (before.Status != OrganizationStatus.Pending)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.NotPendingReview);
        }

        string? licenseUrl = null;
        DateTime? licenseExpires = null;
        if (before.BusinessLicenseDocumentMediaId.HasValue)
        {
            var accessResult = await mediaUrlResolver.ResolvePrivateAccessUrlAsync(before.BusinessLicenseDocumentMediaId.Value, cancellationToken);
            if (accessResult.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(accessResult.Error);
            }
            licenseUrl = accessResult.Value.Url;
            licenseExpires = accessResult.Value.ExpiresAtUtc.UtcDateTime;
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }
        if (org.BusinessLicenseDocumentMediaId != before.BusinessLicenseDocumentMediaId)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.ConcurrentModification);
        }

        // Nguyên tắc bốn mắt: admin là thành viên (Owner) của tổ chức thì phải để admin khác duyệt.
        if (await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken) is not null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SelfReviewNotAllowed);
        }

        if (org.Status != OrganizationStatus.Pending)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.NotPendingReview);
        }

        if (decision == OrganizationStatus.Active)
        {
            if (await organizationRepository.SlugExistsAsync(org.Slug, cancellationToken))
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SlugAlreadyExists);
            }
            if (org.TaxCode is not null && await organizationRepository.TaxCodeExistsAsync(org.TaxCode, cancellationToken))
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Organization.TaxCodeAlreadyExists);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        org.Status = decision;
        org.RejectionReason = rejectionReason;
        org.ReviewedByUserId = actorUserId;
        org.ReviewedAtUtc = now.UtcDateTime;

        await organizationRepository.UpdateAsync(org, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = auditAction,
            Result = rejectionReason is null ? "Success" : $"Success: {rejectionReason}",
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = now
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        string? logoUrl = mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId);
        return Result.Success(org.ToResponse(logoUrl, licenseUrl, licenseExpires));
    }


    // Cùng nguồn quyền toàn cục với OrganizationService: membership trước, fallback về UserRole.
    private async Task<bool> IsPlatformAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> perms = await membershipRepository.GetGlobalPermissionsAsync(userId, cancellationToken);
        if (perms.Count == 0)
        {
            perms = await roleRepository.GetUserPermissionCodesAsync(userId, cancellationToken);
        }

        return perms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);
    }
}
