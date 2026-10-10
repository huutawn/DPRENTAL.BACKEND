using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Aggregate root representing a customer's rental order.
/// Focuses purely on data, relationships, localized invariants, and collection management.
/// Business workflows and state transitions are decoupled into application services.
/// </summary>
[Table("rental_orders", Schema = "rental")]
[Index(nameof(OrderCode), IsUnique = true)]
[Index(nameof(RenterId))]
[Index(nameof(Status))]
[Index(nameof(PaymentDeadline))]
public class RentalOrder
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OrderCode { get; private set; } = string.Empty;

    public Guid RenterId { get; private set; }

    public RentalOrderStatus Status { get; private set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; private set; } = "VND";

    [Required]
    [MaxLength(64)]
    public string DeliveryMethod { get; private set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string RecipientName { get; private set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string RecipientPhone { get; private set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string DeliveryAddress { get; private set; } = string.Empty;

    [MaxLength(2000)]
    public string? DeliveryNote { get; private set; }

    [MaxLength(256)]
    public string RefundDestinationRef { get; private set; } = "ORIGINAL_PAYMENT_SOURCE";

    [Required]
    [MaxLength(64)]
    public string TermsVersion { get; private set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string TermsDocumentRef { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TermsContentHash { get; private set; } = string.Empty;

    public DateTimeOffset PaymentDeadline { get; private set; }

    [MaxLength(256)]
    public string? InvoiceRef { get; private set; }

    [MaxLength(256)]
    public string? PaymentRef { get; private set; }

    public DateTimeOffset? PaymentConfirmedAt { get; private set; }

    public bool LatePaymentRefundRequired { get; private set; }

    [Required]
    [MaxLength(64)]
    public string CheckoutOperationId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    [ConcurrencyCheck]
    public long Version { get; private set; }

    private readonly List<RentalItem> _items = [];
    public IReadOnlyCollection<RentalItem> Items => _items.AsReadOnly();

    [NotMapped]
    public decimal TotalRentalAmount => _items.Sum(i => i.RentalAmount);

    [NotMapped]
    public decimal TotalDepositAmount => _items.Sum(i => i.DepositAmount);

    [NotMapped]
    public decimal TotalPayableAmount => TotalRentalAmount + TotalDepositAmount;

    public RentalTermsAcceptance? TermsAcceptance { get; private set; }

    private readonly List<RentalStateTransition> _stateTransitions = [];
    public IReadOnlyCollection<RentalStateTransition> StateTransitions => _stateTransitions.AsReadOnly();

    private readonly List<RentalOperationReceipt> _operationReceipts = [];
    public IReadOnlyCollection<RentalOperationReceipt> OperationReceipts => _operationReceipts.AsReadOnly();

    private RentalOrder()
    {
    }

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
        string termsVersion,
        string termsDocumentRef,
        string termsContentHash,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        IEnumerable<InitialRentalItemDto> initialItems) =>
        Create(
            id,
            orderCode,
            renterId,
            currency,
            deliveryMethod,
            recipientName,
            recipientPhone,
            deliveryAddress,
            deliveryNote,
            null,
            termsVersion,
            termsDocumentRef,
            termsContentHash,
            checkoutOperationId,
            utcNow,
            initialItems);

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
        string? refundDestinationRef,
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

        if (string.IsNullOrWhiteSpace(currency) || !string.Equals(currency.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
        {
            return RentalErrors.CurrencyMismatch;
        }

        if (string.IsNullOrWhiteSpace(deliveryMethod) || deliveryMethod.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(recipientName) || recipientName.Trim().Length > 200 ||
            string.IsNullOrWhiteSpace(recipientPhone) || recipientPhone.Trim().Length > 32 ||
            string.IsNullOrWhiteSpace(deliveryAddress) || deliveryAddress.Trim().Length > 500 ||
            string.IsNullOrWhiteSpace(termsVersion) || termsVersion.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(termsDocumentRef) || termsDocumentRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(termsContentHash) || termsContentHash.Trim().Length > 128 ||
            string.IsNullOrWhiteSpace(checkoutOperationId) || checkoutOperationId.Trim().Length > 64)
        {
            return RentalErrors.InvalidSnapshot;
        }

        string effectiveRefundDestination = string.IsNullOrWhiteSpace(refundDestinationRef)
            ? "ORIGINAL_PAYMENT_SOURCE"
            : refundDestinationRef.Trim();

        if (effectiveRefundDestination.Length > 256)
        {
            return RentalErrors.StringLengthExceeded;
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

            var rateCheck = VndAmountValidator.ValidateVndAmount(itemDto.DailyRateSnapshot, "Giá thuê theo ngày");
            if (rateCheck.IsFailure) return rateCheck.Error;

            var depositCheck = VndAmountValidator.ValidateVndAmount(itemDto.DepositPerUnitSnapshot, "Tiền cọc mỗi thiết bị");
            if (depositCheck.IsFailure) return depositCheck.Error;

            if (itemDto.ExpectedStartDate >= itemDto.ExpectedEndDate)
            {
                return RentalErrors.InvalidRentalPeriod;
            }

            int rentalDays = itemDto.ExpectedEndDate.DayNumber - itemDto.ExpectedStartDate.DayNumber;
            decimal calculatedRental = itemDto.DailyRateSnapshot * itemDto.Quantity * rentalDays;
            decimal calculatedDeposit = itemDto.DepositPerUnitSnapshot * itemDto.Quantity;

            var rentalAmountCheck = VndAmountValidator.ValidateVndAmount(calculatedRental, "Tiền thuê tính toán");
            if (rentalAmountCheck.IsFailure) return rentalAmountCheck.Error;

            var depositAmountCheck = VndAmountValidator.ValidateVndAmount(calculatedDeposit, "Tiền cọc tính toán");
            if (depositAmountCheck.IsFailure) return depositAmountCheck.Error;

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

        decimal totalOrderPayable = itemsList.Sum(i =>
            (i.DailyRateSnapshot * i.Quantity * (i.ExpectedEndDate.DayNumber - i.ExpectedStartDate.DayNumber)) +
            (i.DepositPerUnitSnapshot * i.Quantity));

        var totalOrderCheck = VndAmountValidator.ValidateVndAmount(totalOrderPayable, "Tổng tiền đơn thuê");
        if (totalOrderCheck.IsFailure) return totalOrderCheck.Error;

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
            RefundDestinationRef = effectiveRefundDestination,
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

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            id,
            checkoutOperationId.Trim(),
            "CreateOrder",
            $"create-order:{order.OrderCode}",
            true,
            null,
            null,
            utcNow));

        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            id,
            null,
            null,
            RentalOrderStatus.PendingPayment.ToString(),
            "CreateOrder",
            renterId,
            ActorRole.Renter,
            "Checkout payable order",
            utcNow,
            checkoutOperationId.Trim()));

        return order;
    }

    public void AddRentalItem(RentalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_items.Any(i => i.Id == item.Id))
        {
            throw new InvalidOperationException($"RentalItem '{item.Id}' already exists in order.");
        }
        _items.Add(item);
    }

    public void SetTermsAcceptance(RentalTermsAcceptance termsAcceptance)
    {
        ArgumentNullException.ThrowIfNull(termsAcceptance);
        TermsAcceptance = termsAcceptance;
    }

    public void AddStateTransition(RentalStateTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        _stateTransitions.Add(transition);
    }

    public void AddOperationReceipt(RentalOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _operationReceipts.Add(receipt);
    }

    internal void IncrementVersion()
    {
        Version++;
    }

    public bool HasInFlightAccept(Guid itemId)
    {
        return _operationReceipts.Any(r =>
            r.Action == "SupplierAccept" &&
            r.TargetItemId == itemId &&
            (r.Status == RentalOperationReceipt.StatusProcessing ||
             r.ErrorClassification is RentalOperationReceipt.ClassificationUncertain or RentalOperationReceipt.ClassificationUncertainExhausted) &&
            !r.IsSuccess);
    }
    internal Result MarkAsPaid(string invoiceRef, string paymentRef, DateTimeOffset paidAt, DateTimeOffset utcNow)
    {
        if (Status != RentalOrderStatus.PendingPayment)
        {
            return Error.Conflict("Order.NotPendingPayment", $"Đơn hàng không ở trạng thái PendingPayment (trạng thái hiện tại: {Status}).");
        }

        string from = Status.ToString();
        Status = RentalOrderStatus.Paid;
        InvoiceRef = invoiceRef;
        PaymentRef = paymentRef;
        PaymentConfirmedAt = paidAt;
        UpdatedAt = utcNow;
        Version++;
        foreach (var item in _items)
        {
            item.RecordPaid(paidAt);
        }

        AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            Id,
            null,
            from,
            RentalOrderStatus.Paid.ToString(),
            "InvoicePaid",
            null,
            ActorRole.System,
            "Order payment confirmed by PaymentService",
            utcNow,
            paymentRef));

        return Result.Success();
    }

    internal void MarkAsLatePayment(string invoiceRef, string paymentRef, DateTimeOffset paidAt, DateTimeOffset utcNow)
    {
        LatePaymentRefundRequired = true;
        InvoiceRef = invoiceRef;
        PaymentRef = paymentRef;
        PaymentConfirmedAt = paidAt;
        UpdatedAt = utcNow;
        Version++;
        AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            Id,
            null,
            Status.ToString(),
            Status.ToString(),
            "ConfirmPaymentLate",
            null,
            ActorRole.System,
            "Payment confirmed after deadline or on terminal order; full refund required",
            utcNow,
            paymentRef));
    }

    internal void EvaluateOrderStatusRollup(DateTimeOffset utcNow, string operationId)
    {
        if (Status != RentalOrderStatus.Paid && Status != RentalOrderStatus.AwaitingHandover)
        {
            return;
        }

        // If any item is still waiting for supplier decision, Order remains in Paid status
        if (_items.Any(i => i.Status == RentalItemStatus.PendingSupplierConfirmation))
        {
            return;
        }

        // If all items finished supplier decision:
        // 1. Any item is accepted/awaiting handover (or beyond) -> Order moves to AwaitingHandover
        if (_items.Any(i => i.Status is RentalItemStatus.AwaitingHandover or RentalItemStatus.InRental or RentalItemStatus.PendingSettlement or RentalItemStatus.Completed))
        {
            if (Status != RentalOrderStatus.AwaitingHandover)
            {
                string from = Status.ToString();
                Status = RentalOrderStatus.AwaitingHandover;
                UpdatedAt = utcNow;
                Version++;
                AddStateTransition(new RentalStateTransition(
                    Guid.NewGuid(),
                    Id,
                    null,
                    from,
                    RentalOrderStatus.AwaitingHandover.ToString(),
                    "OrderRollup",
                    null,
                    ActorRole.System,
                    "Supplier confirmation completed with accepted items; awaiting handover",
                    utcNow,
                    operationId));
            }
            return;
        }

        // 2. All items are Rejected -> Order moves to Rejected
        if (_items.All(i => i.Status == RentalItemStatus.Rejected))
        {
            if (Status != RentalOrderStatus.Rejected)
            {
                string from = Status.ToString();
                Status = RentalOrderStatus.Rejected;
                UpdatedAt = utcNow;
                Version++;
                AddStateTransition(new RentalStateTransition(
                    Guid.NewGuid(),
                    Id,
                    null,
                    from,
                    RentalOrderStatus.Rejected.ToString(),
                    "OrderRollup",
                    null,
                    ActorRole.System,
                    "All items rejected by supplier",
                    utcNow,
                    operationId));
            }
            return;
        }

        // 3. All items ended in reject/expiry and at least one is Expired -> Order moves to Expired
        if (_items.All(i => i.Status is RentalItemStatus.Rejected or RentalItemStatus.Expired) &&
            _items.Any(i => i.Status == RentalItemStatus.Expired))
        {
            if (Status != RentalOrderStatus.Expired)
            {
                string from = Status.ToString();
                Status = RentalOrderStatus.Expired;
                UpdatedAt = utcNow;
                Version++;
                AddStateTransition(new RentalStateTransition(
                    Guid.NewGuid(),
                    Id,
                    null,
                    from,
                    RentalOrderStatus.Expired.ToString(),
                    "OrderRollup",
                    null,
                    ActorRole.System,
                    "Items ended in reject/expiry without any accepted items",
                    utcNow,
                    operationId));
            }
            return;
        }
    }

    internal void MarkPaymentExpired(DateTimeOffset utcNow, string operationId)
    {
        string from = Status.ToString();
        Status = RentalOrderStatus.Expired;
        UpdatedAt = utcNow;
        Version++;
        foreach (var item in _items)
        {
            item.ApplyHoldExpired(utcNow, operationId, "Payment deadline expired");
        }

        AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            Id,
            null,
            from,
            RentalOrderStatus.Expired.ToString(),
            "ExpirePayment",
            null,
            ActorRole.System,
            "Payment window expired without payment",
            utcNow,
            operationId));
    }
}
