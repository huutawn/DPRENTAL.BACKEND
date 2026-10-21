using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
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

    private readonly List<RentalSubOrder> _subOrders = [];
    public IReadOnlyCollection<RentalSubOrder> SubOrders => _subOrders.AsReadOnly();

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

    /// <summary>Các trường đều không bắt buộc; trường nào có thì phải hợp lệ (không rỗng, không quá dài).</summary>
    private static bool IsValidListingSnapshot(RentalItemListingSnapshotDto? snapshot)
    {
        if (snapshot is null ||
            snapshot.EquipmentTypeId == Guid.Empty ||
            snapshot.BrandId == Guid.Empty ||
            snapshot.ThumbnailMediaId == Guid.Empty ||
            snapshot.SpecSchemaVersion < 1)
        {
            return false;
        }

        if (!IsValidOptionalText(snapshot.EquipmentTypeName, 200) ||
            !IsValidOptionalText(snapshot.BrandName, 200) ||
            !IsValidOptionalText(snapshot.Model, 200))
        {
            return false;
        }

        if (snapshot.SpecValuesJson is null)
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot.SpecValuesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidOptionalText(string? value, int maxLength) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength);

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
        IEnumerable<InitialRentalItemDto> initialItems,
        DateTimeOffset? paymentDeadline = null)
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
                !IsValidOptionalText(itemDto.PickupLocationSnapshot, RentalItem.MaxPickupLocationSnapshotLength) ||
                string.IsNullOrWhiteSpace(itemDto.AllocationGroupId) || itemDto.AllocationGroupId.Trim().Length > 64)
            {
                return RentalErrors.InvalidSnapshot;
            }

            if (itemDto.ListingDescriptionSnapshot is not null && itemDto.ListingDescriptionSnapshot.Trim().Length > 2000)
            {
                return RentalErrors.InvalidSnapshot;
            }

            if (!IsValidListingSnapshot(itemDto.ListingSnapshot))
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
            PaymentDeadline = paymentDeadline ?? utcNow.AddMinutes(30),
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

        // Mỗi nhà cung cấp một đơn con, đánh số theo thứ tự xuất hiện đầu tiên trong đơn.
        Dictionary<Guid, RentalSubOrder> subOrdersBySupplier = [];
        foreach (var itemDto in itemsList)
        {
            if (!subOrdersBySupplier.TryGetValue(itemDto.SupplierId, out RentalSubOrder? subOrder))
            {
                subOrder = new RentalSubOrder(
                    Guid.NewGuid(),
                    id,
                    itemDto.SupplierId,
                    RentalSubOrder.BuildCode(order.OrderCode, subOrdersBySupplier.Count + 1),
                    utcNow);
                subOrdersBySupplier.Add(itemDto.SupplierId, subOrder);
                order._subOrders.Add(subOrder);
            }

            RentalItem item = new(
                itemDto.Id,
                id,
                subOrder.Id,
                itemDto.EquipmentListingId,
                itemDto.SupplierId,
                itemDto.Quantity,
                itemDto.ExpectedStartDate,
                itemDto.ExpectedEndDate,
                itemDto.ListingTitleSnapshot.Trim(),
                itemDto.ListingDescriptionSnapshot?.Trim(),
                itemDto.PickupLocationSnapshot?.Trim(),
                itemDto.DailyRateSnapshot,
                itemDto.DepositPerUnitSnapshot,
                itemDto.AllocationGroupId.Trim(),
                itemDto.Assignments,
                itemDto.ListingSnapshot);

            item.RentalOrder = order;
            order._items.Add(item);
            subOrder.AddItem(item);
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
        item.RentalOrder = this;
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

    /// <summary>Gán ghi chú giao hàng mới (null là xóa). Kiểm tra quyền và thời điểm được sửa nằm ở Application.</summary>
    public void UpdateDeliveryNote(string? note, DateTimeOffset utcNow)
    {
        DeliveryNote = note;
        RecordReadModelUpdate(utcNow);
    }

    internal void RecordReadModelUpdate(DateTimeOffset utcNow)
    {
        Version++;
        UpdatedAt = utcNow;
    }

    internal void IncrementVersion()
    {
        RecordReadModelUpdate(UpdatedAt);
    }

    public bool HasInFlightAccept(Guid itemId)
    {
        return _operationReceipts.Any(r =>
            r.Action == "SupplierAccept" &&
            r.TargetItemId == itemId &&
            (r.Status == RentalOperationReceipt.StatusProcessing ||
             r.Status == RentalOperationReceipt.StatusPending ||
             r.IsOutcomeUncertain ||
             r.ErrorClassification is RentalOperationReceipt.ClassificationUncertain or RentalOperationReceipt.ClassificationUncertainExhausted) &&
            !r.IsSuccess);
    }

    /// <summary>
    /// Gắn hóa đơn Payment vào đơn đang chờ thanh toán. Gắn lại đúng hóa đơn cũ là hợp lệ; hóa đơn khác thì từ chối.
    /// </summary>
    internal Result AttachInvoice(string invoiceRef, DateTimeOffset utcNow)
    {
        if (Status != RentalOrderStatus.PendingPayment)
        {
            return Error.Conflict("Order.NotPendingPayment", $"Đơn hàng không ở trạng thái PendingPayment (trạng thái hiện tại: {Status}).");
        }

        if (string.IsNullOrWhiteSpace(invoiceRef) || invoiceRef.Trim().Length > 256)
        {
            return Error.Validation("Order.InvalidInvoiceRef", "InvoiceRef không được rỗng và tối đa 256 ký tự.");
        }

        if (!string.IsNullOrWhiteSpace(InvoiceRef))
        {
            return string.Equals(InvoiceRef, invoiceRef.Trim(), StringComparison.OrdinalIgnoreCase)
                ? Result.Success()
                : Error.Conflict("Order.InvoiceAlreadyAttached", "Đơn hàng đã được gắn với một hóa đơn khác.");
        }

        InvoiceRef = invoiceRef.Trim();
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
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
        // Supplier nhận hoặc từ chối cả đơn con, nên mọi dòng của đơn con dùng chung hạn sớm nhất.
        foreach (var subOrderItems in _items.GroupBy(i => i.RentalSubOrderId))
        {
            DateTimeOffset deadline = subOrderItems.Min(i =>
                RentalItem.CalculateSupplierConfirmationDeadline(paidAt, i.ExpectedStartDate));
            foreach (var item in subOrderItems)
            {
                item.RecordPaid(deadline);
            }
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

    internal void RecordPaymentReceived(string invoiceRef, string paymentRef, DateTimeOffset paidAt, DateTimeOffset utcNow)
    {
        InvoiceRef = invoiceRef;
        PaymentRef = paymentRef;
        PaymentConfirmedAt = paidAt;
        UpdatedAt = utcNow;
    }

    internal void ClearLatePaymentFlag()
    {
        LatePaymentRefundRequired = false;
    }

    internal void MarkAsLatePayment(string invoiceRef, string paymentRef, DateTimeOffset paidAt, DateTimeOffset utcNow)
    {
        LatePaymentRefundRequired = true;
        InvoiceRef = invoiceRef;
        PaymentRef = paymentRef;
        PaymentConfirmedAt = paidAt;
        UpdatedAt = utcNow;

        if (Status == RentalOrderStatus.PendingPayment)
        {
            string from = Status.ToString();
            Status = RentalOrderStatus.Expired;
            foreach (var item in _items)
            {
                item.ApplyHoldExpired(utcNow, paymentRef, "Payment deadline expired");
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
                "Order expired due to late payment",
                utcNow,
                paymentRef));
        }

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

    internal void ApplyStatusRollup(RentalOrderStatus newStatus, DateTimeOffset utcNow, string operationId, string? reason = null)
    {
        if (Status == newStatus)
        {
            return;
        }

        string from = Status.ToString();
        Status = newStatus;
        UpdatedAt = utcNow;
        AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            Id,
            null,
            from,
            newStatus.ToString(),
            "OrderRollup",
            null,
            ActorRole.System,
            reason ?? $"Order status rolled up to {newStatus}",
            utcNow,
            operationId));
    }

    internal void MarkPaymentExpired(DateTimeOffset utcNow, string operationId)
    {
        string from = Status.ToString();
        Status = RentalOrderStatus.Expired;
        UpdatedAt = utcNow;
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

    internal void MarkCancelled(
        DateTimeOffset utcNow,
        string operationId,
        string reason = "Order cancelled",
        Guid? actorId = null,
        ActorRole actorRole = ActorRole.System)
    {
        string from = Status.ToString();
        Status = RentalOrderStatus.Cancelled;
        UpdatedAt = utcNow;
        foreach (var item in _items)
        {
            item.ApplyCancelled(utcNow, operationId, reason, actorId, actorRole);
        }

        AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            Id,
            null,
            from,
            RentalOrderStatus.Cancelled.ToString(),
            "CancelOrder",
            actorId,
            actorRole,
            reason,
            utcNow,
            operationId));
    }
}
