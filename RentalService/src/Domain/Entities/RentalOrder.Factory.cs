using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalOrder
{
    public static Result<RentalOrder> Create(
        Guid id,
        string orderCode,
        Guid renterId,
        string currency,
        string deliveryMethod,
        string recipientName,
        string recipientPhone,
        string deliveryAddress,
        string? deliveryNote,
        string refundDestinationRef,
        string termsVersion,
        string termsDocumentRef,
        string termsContentHash,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        IEnumerable<InitialRentalItemDto> initialItems)
    {
        if (id == Guid.Empty || renterId == Guid.Empty)
        {
            return RentalErrors.InvalidSnapshot;
        }

        if (string.IsNullOrWhiteSpace(orderCode) || orderCode.Trim().Length > 64)
        {
            return RentalErrors.InvalidSnapshot;
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            return RentalErrors.CurrencyMismatch;
        }

        if (string.IsNullOrWhiteSpace(deliveryMethod) || deliveryMethod.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(recipientName) || recipientName.Trim().Length > 200 ||
            string.IsNullOrWhiteSpace(recipientPhone) || recipientPhone.Trim().Length > 32 ||
            string.IsNullOrWhiteSpace(deliveryAddress) || deliveryAddress.Trim().Length > 500 ||
            string.IsNullOrWhiteSpace(refundDestinationRef) || refundDestinationRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(termsVersion) || termsVersion.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(termsDocumentRef) || termsDocumentRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(termsContentHash) || termsContentHash.Trim().Length > 128 ||
            string.IsNullOrWhiteSpace(checkoutOperationId) || checkoutOperationId.Trim().Length > 64)
        {
            return RentalErrors.InvalidSnapshot;
        }

        if (deliveryNote is not null && deliveryNote.Trim().Length > 2000)
        {
            return RentalErrors.InvalidSnapshot;
        }

        var itemsList = initialItems?.ToList() ?? [];
        if (itemsList.Count == 0)
        {
            return RentalErrors.CartEmpty;
        }

        if (itemsList.Select(i => i.Id).Distinct().Count() != itemsList.Count)
        {
            return RentalErrors.DuplicateId;
        }

        HashSet<Guid> seenAssignmentIds = [];
        HashSet<Guid> seenAssetIds = [];
        HashSet<Guid> seenAllocationIds = [];

        foreach (var itemDto in itemsList)
        {
            if (itemDto.Id == Guid.Empty || itemDto.EquipmentListingId == Guid.Empty || itemDto.SupplierId == Guid.Empty)
            {
                return RentalErrors.InvalidSnapshot;
            }

            if (itemDto.Quantity <= 0)
            {
                return RentalErrors.InvalidQuantity;
            }

            if (itemDto.DailyRateSnapshot < 0 || itemDto.DepositPerUnitSnapshot < 0)
            {
                return RentalErrors.NegativeAmount;
            }

            if (itemDto.ExpectedStartDate >= itemDto.ExpectedEndDate)
            {
                return RentalErrors.InvalidRentalPeriod;
            }

            if (string.IsNullOrWhiteSpace(itemDto.ListingTitleSnapshot) || itemDto.ListingTitleSnapshot.Trim().Length > 200 ||
                string.IsNullOrWhiteSpace(itemDto.PickupLocationSnapshot) || itemDto.PickupLocationSnapshot.Trim().Length > 500 ||
                string.IsNullOrWhiteSpace(itemDto.AllocationGroupId) || itemDto.AllocationGroupId.Trim().Length > 64)
            {
                return RentalErrors.InvalidSnapshot;
            }

            if (itemDto.ListingDescriptionSnapshot is not null && itemDto.ListingDescriptionSnapshot.Trim().Length > 2000)
            {
                return RentalErrors.InvalidSnapshot;
            }

            var assignments = itemDto.Assignments?.ToList() ?? [];
            if (assignments.Count != itemDto.Quantity)
            {
                return RentalErrors.QuantityAssignmentMismatch;
            }

            foreach (var a in assignments)
            {
                if (a.Id == Guid.Empty || a.AssetId == Guid.Empty || a.AllocationId == Guid.Empty)
                {
                    return RentalErrors.InvalidSnapshot;
                }

                if (!seenAssignmentIds.Add(a.Id) || !seenAssetIds.Add(a.AssetId) || !seenAllocationIds.Add(a.AllocationId))
                {
                    return RentalErrors.DuplicateAssignment;
                }
            }
        }

        RentalOrder order = new()
        {
            Id = id,
            OrderCode = orderCode.Trim(),
            RenterId = renterId,
            Status = RentalOrderStatus.PendingPayment,
            Currency = currency.Trim().ToUpperInvariant(),
            DeliveryMethod = deliveryMethod.Trim(),
            RecipientName = recipientName.Trim(),
            RecipientPhone = recipientPhone.Trim(),
            DeliveryAddress = deliveryAddress.Trim(),
            DeliveryNote = deliveryNote?.Trim(),
            RefundDestinationRef = refundDestinationRef.Trim(),
            TermsVersion = termsVersion.Trim(),
            TermsDocumentRef = termsDocumentRef.Trim(),
            TermsContentHash = termsContentHash.Trim(),
            PaymentDeadline = utcNow.AddMinutes(30),
            CheckoutOperationId = checkoutOperationId.Trim(),
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            Version = 1
        };

        order.TermsAcceptance = new RentalTermsAcceptance(
            Guid.NewGuid(),
            id,
            renterId,
            termsVersion.Trim(),
            termsContentHash.Trim(),
            utcNow);

        foreach (var itemDto in itemsList)
        {
            RentalItem item = new(
                itemDto.Id,
                id,
                itemDto.EquipmentListingId,
                itemDto.SupplierId,
                itemDto.Quantity,
                itemDto.ExpectedStartDate,
                itemDto.ExpectedEndDate,
                itemDto.ListingTitleSnapshot.Trim(),
                itemDto.ListingDescriptionSnapshot?.Trim(),
                itemDto.PickupLocationSnapshot.Trim(),
                itemDto.DailyRateSnapshot,
                itemDto.DepositPerUnitSnapshot,
                itemDto.AllocationGroupId.Trim(),
                itemDto.Assignments);

            order._items.Add(item);
        }

        string checkoutFingerprint = ComputeFingerprint("CreateOrder", null, ActorRole.Renter, renterId, order.OrderCode, order.Currency, order.CheckoutOperationId);
        order._operationReceipts.Add(new RentalOperationReceipt(
            Guid.NewGuid(),
            id,
            checkoutOperationId.Trim(),
            "CreateOrder",
            checkoutFingerprint,
            true,
            null,
            null,
            utcNow));

        order.LogTransition(
            null,
            null,
            RentalOrderStatus.PendingPayment.ToString(),
            "CreateOrder",
            renterId,
            ActorRole.Renter,
            "Checkout payable order",
            utcNow,
            checkoutOperationId.Trim());

        return order;
    }
}
