using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Services;

public sealed class ListingOrganizationConfirmationService(
    IEquipmentListingRepository listings, IInventoryWriteTransaction transaction,
    IOrganizationMembershipClient membershipClient, TimeProvider timeProvider)
{
    // Values follow Auth's OrganizationType, OrganizationStatus and MembershipStatus contracts.
    private const int SupplierType = 1;
    private const int ActiveOrganization = 1;
    private const int ActiveMembership = 1;

    public async Task<Result<Guid>> ConfirmAsync(Guid listingId, Guid organizationId, IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        if (!caller.IsHuman || !caller.UserId.HasValue) return Error.Forbidden("FORBIDDEN", "Chỉ chủ listing được xác nhận tổ chức.");
        if (listingId == Guid.Empty) return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        if (organizationId == Guid.Empty) return Error.Validation("INVALID_ORGANIZATION_ID", "Organization ID không hợp lệ.");
        var snapshot = await listings.GetByIdForOwnerAsync(listingId, caller.UserId.Value, cancellationToken);
        if (snapshot is null) return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại hoặc không thuộc người dùng.");

        var memberships = await membershipClient.GetCurrentAsync(cancellationToken);
        if (memberships.IsFailure) return memberships.Error;
        bool verified = memberships.Value.Any(m => m.OrganizationId == organizationId &&
            string.Equals(m.RoleName, "Owner", StringComparison.OrdinalIgnoreCase) &&
            m.Type == SupplierType && m.OrganizationStatus == ActiveOrganization && m.MembershipStatus == ActiveMembership);
        if (!verified) return Error.Forbidden("ORGANIZATION_NOT_VERIFIED", "Bạn không phải owner của tổ chức Supplier đang hoạt động.");

        return await transaction.ExecuteListingWriteAsync(listingId, async (context, ct) =>
        {
            var listing = await context.Listings.GetByIdForOwnerAsync(listingId, caller.UserId.Value, ct);
            if (listing is null) return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại hoặc không thuộc người dùng.");
            if (listing.OrganizationId.HasValue && listing.OrganizationId != organizationId)
                return Error.Conflict("LISTING_ORGANIZATION_ALREADY_SET", "Listing đã được gắn với tổ chức khác.");
            if (!listing.OrganizationId.HasValue)
            {
                var now = timeProvider.GetUtcNow();
                listing.AssignOrganization(organizationId, now);
                context.Listings.Update(listing);
                var revision = await context.GetNextSearchRevisionAsync(ct);
                context.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(
                    listing.Id, "Upsert", revision, false, now, context.SearchBackfillRunId));
                context.StageCacheInvalidation([$"listing:{listingId:D}", $"owner:{caller.UserId.Value:D}", "public-discovery"]);
            }
            return Result.Success(organizationId);
        }, cancellationToken);
    }
}
