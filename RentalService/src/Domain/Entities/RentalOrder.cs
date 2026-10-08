using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_orders", Schema = "rental")]
[Index(nameof(OrderCode), IsUnique = true)]
[Index(nameof(CheckoutOperationId), IsUnique = true)]
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

    [Required]
    [MaxLength(256)]
    public string RefundDestinationRef { get; private set; } = string.Empty;

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

    [Required]
    [MaxLength(64)]
    public string CheckoutOperationId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    [ConcurrencyCheck]
    public long Version { get; private set; }

    private readonly List<RentalItem> _items = [];
    public IReadOnlyCollection<RentalItem> Items => _items.AsReadOnly();

    public RentalTermsAcceptance? TermsAcceptance { get; private set; }

    private readonly List<RentalStateTransition> _stateTransitions = [];
    public IReadOnlyCollection<RentalStateTransition> StateTransitions => _stateTransitions.AsReadOnly();

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

        if (string.IsNullOrWhiteSpace(orderCode))
        {
            return RentalErrors.InvalidSnapshot;
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            return RentalErrors.CurrencyMismatch;
        }

        if (string.IsNullOrWhiteSpace(deliveryMethod) ||
            string.IsNullOrWhiteSpace(recipientName) ||
            string.IsNullOrWhiteSpace(recipientPhone) ||
            string.IsNullOrWhiteSpace(deliveryAddress) ||
            string.IsNullOrWhiteSpace(refundDestinationRef) ||
            string.IsNullOrWhiteSpace(termsVersion) ||
            string.IsNullOrWhiteSpace(termsDocumentRef) ||
            string.IsNullOrWhiteSpace(termsContentHash) ||
            string.IsNullOrWhiteSpace(checkoutOperationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        var itemsList = initialItems?.ToList() ?? [];
        if (itemsList.Count == 0)
        {
            return RentalErrors.CartEmpty;
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
            if (itemDto.Quantity <= 0)
            {
                return RentalErrors.InvalidQuantity;
            }

            if (itemDto.ExpectedStartDate >= itemDto.ExpectedEndDate)
            {
                return RentalErrors.InvalidRentalPeriod;
            }

            var assignments = itemDto.Assignments?.ToList() ?? [];
            if (assignments.Count != itemDto.Quantity)
            {
                return RentalErrors.QuantityAssignmentMismatch;
            }

            RentalItem item = new(
                itemDto.Id,
                id,
                itemDto.EquipmentListingId,
                itemDto.SupplierId,
                itemDto.Quantity,
                itemDto.ExpectedStartDate,
                itemDto.ExpectedEndDate,
                itemDto.ListingTitleSnapshot,
                itemDto.ListingDescriptionSnapshot,
                itemDto.PickupLocationSnapshot,
                itemDto.DailyRateSnapshot,
                itemDto.DepositPerUnitSnapshot,
                itemDto.AllocationGroupId,
                assignments);

            order._items.Add(item);
        }

        order.LogTransition(
            null,
            null,
            RentalOrderStatus.PendingPayment.ToString(),
            "CreateOrder",
            renterId,
            ActorRole.Renter,
            "Checkout payable order",
            utcNow,
            checkoutOperationId);

        return order;
    }

    public Result ConfirmPayment(
        string invoiceRef,
        string paymentRef,
        DateTimeOffset confirmedAt,
        TimeSpan supplierConfirmationTimeout,
        string operationId)
    {
        if (Status != RentalOrderStatus.PendingPayment)
        {
            return RentalErrors.OrderAlreadyPaid;
        }

        if (confirmedAt > PaymentDeadline)
        {
            return RentalErrors.PaymentExpired;
        }

        InvoiceRef = invoiceRef.Trim();
        PaymentRef = paymentRef.Trim();
        PaymentConfirmedAt = confirmedAt;

        DateTimeOffset supplierDeadline = confirmedAt.Add(supplierConfirmationTimeout);

        string fromState = Status.ToString();
        foreach (var item in _items)
        {
            string itemFromState = item.Status.ToString();
            item.SetPaymentConfirmed(supplierDeadline);
            LogTransition(
                item.Id,
                itemFromState,
                item.Status.ToString(),
                "InvoicePaid",
                null,
                ActorRole.System,
                "Payment confirmed",
                confirmedAt,
                operationId);
        }

        RecalculateStatus();
        UpdatedAt = confirmedAt;
        Version++;

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "InvoicePaid",
            null,
            ActorRole.System,
            "Order payment confirmed",
            confirmedAt,
            operationId);

        return Result.Success();
    }

    public Result ExpirePayment(DateTimeOffset utcNow, string operationId)
    {
        if (Status != RentalOrderStatus.PendingPayment)
        {
            return RentalErrors.InvalidOrderStatus;
        }

        string fromState = Status.ToString();
        foreach (var item in _items)
        {
            string itemFrom = item.Status.ToString();
            item.SetPaymentExpired();
            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "PaymentTimeout",
                null,
                ActorRole.System,
                "Payment deadline expired",
                utcNow,
                operationId);
        }

        Status = RentalOrderStatus.Expired;
        UpdatedAt = utcNow;
        Version++;

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "PaymentTimeout",
            null,
            ActorRole.System,
            "Order payment expired",
            utcNow,
            operationId);

        return Result.Success();
    }

    public Result CancelByRenter(Guid renterId, string reason, DateTimeOffset utcNow, string operationId)
    {
        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (Status != RentalOrderStatus.PendingPayment)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        string fromState = Status.ToString();
        foreach (var item in _items)
        {
            string itemFrom = item.Status.ToString();
            item.SetCancelled();
            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "RenterCancel",
                renterId,
                ActorRole.Renter,
                reason,
                utcNow,
                operationId);
        }

        Status = RentalOrderStatus.Cancelled;
        UpdatedAt = utcNow;
        Version++;

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "RenterCancel",
            renterId,
            ActorRole.Renter,
            reason,
            utcNow,
            operationId);

        return Result.Success();
    }

    public Result SupplierAcceptItem(Guid itemId, Guid supplierId, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SupplierAccept(supplierId, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "SupplierAccept",
            supplierId,
            ActorRole.Supplier,
            "Supplier accepted item",
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "SupplierAccept",
                supplierId,
                ActorRole.Supplier,
                "Order status updated after supplier acceptance",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result SupplierRejectItem(Guid itemId, Guid supplierId, string reason, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SupplierReject(supplierId, reason, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "SupplierReject",
            supplierId,
            ActorRole.Supplier,
            reason,
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "SupplierReject",
                supplierId,
                ActorRole.Supplier,
                "Order status updated after supplier rejection",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result SupplierTimeoutItem(Guid itemId, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SupplierTimeout(utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "SupplierTimeout",
            null,
            ActorRole.System,
            "Supplier confirmation deadline expired",
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "SupplierTimeout",
                null,
                ActorRole.System,
                "Order status updated after supplier timeout",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result CancelItemBeforeHandover(Guid itemId, string reason, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.CancelBeforeHandover(reason, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "CancelBeforeHandover",
            null,
            ActorRole.System,
            reason,
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "CancelBeforeHandover",
                null,
                ActorRole.System,
                "Order status updated after item cancellation",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result SubmitHandover(
        Guid itemId,
        Guid supplierId,
        DateTimeOffset actualHandoverAt,
        IEnumerable<HandoverObservationDto> observations,
        string? note,
        DateTimeOffset utcNow,
        string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SubmitHandover(Guid.NewGuid(), supplierId, actualHandoverAt, observations, note, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "SubmitHandover",
            supplierId,
            ActorRole.Supplier,
            note ?? "Handover record submitted",
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "SubmitHandover",
                supplierId,
                ActorRole.Supplier,
                "Order status updated after handover",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result AcknowledgeHandover(Guid itemId, Guid renterId, DateTimeOffset acknowledgedAt, string? note, DateTimeOffset utcNow, string operationId)
    {
        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.AcknowledgeHandover(renterId, acknowledgedAt, note);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result SubmitReturn(
        Guid itemId,
        Guid supplierId,
        DateTimeOffset actualReturnAt,
        IEnumerable<ReturnObservationDto> observations,
        string? note,
        DateTimeOffset utcNow,
        string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SubmitReturn(Guid.NewGuid(), supplierId, actualReturnAt, observations, note, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "SubmitReturn",
            supplierId,
            ActorRole.Supplier,
            note ?? "Return record submitted",
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "SubmitReturn",
                supplierId,
                ActorRole.Supplier,
                "Order status updated after return",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    public Result SubmitDeductionProposal(
        Guid itemId,
        Guid supplierId,
        IEnumerable<DeductionLineDto> lines,
        DateTimeOffset utcNow,
        string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.SubmitDeductionProposal(Guid.NewGuid(), supplierId, lines, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result ExpireDeductionSubmission(Guid itemId, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.ExpireDeductionSubmission(utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result AcceptDeductionProposal(Guid itemId, Guid renterId, DateTimeOffset utcNow, string operationId)
    {
        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.AcceptDeductionProposal(renterId, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result TimeoutDeductionProposal(Guid itemId, DateTimeOffset utcNow, string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.TimeoutDeductionProposal(utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result DisputeDeductionProposal(Guid itemId, Guid renterId, string reason, IEnumerable<Guid>? mediaIds, DateTimeOffset utcNow, string operationId)
    {
        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.DisputeDeductionProposal(Guid.NewGuid(), renterId, reason, mediaIds, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result ResolveDispute(
        Guid itemId,
        Guid adminId,
        decimal finalDeductionAmount,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow,
        string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.ResolveDispute(adminId, finalDeductionAmount, reason, mediaIds, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result ConfirmClosuresAndComplete(
        Guid itemId,
        DateTimeOffset inventoryClosureAt,
        DateTimeOffset financialClosureAt,
        string? settlementRef,
        string? refundRef,
        DateTimeOffset utcNow,
        string operationId)
    {
        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.ConfirmClosuresAndComplete(inventoryClosureAt, financialClosureAt, settlementRef, refundRef);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "CompleteItem",
            null,
            ActorRole.System,
            "Closures confirmed and item completed",
            utcNow,
            operationId);

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "CompleteItem",
                null,
                ActorRole.System,
                "Order status updated after item completed",
                utcNow,
                operationId);
        }

        return Result.Success();
    }

    private void RecalculateStatus()
    {
        if (Status is RentalOrderStatus.PendingPayment or RentalOrderStatus.Expired or RentalOrderStatus.Cancelled)
        {
            if (_items.All(i => i.Status == RentalItemStatus.PendingPayment))
            {
                Status = RentalOrderStatus.PendingPayment;
                return;
            }
            if (_items.All(i => i.Status == RentalItemStatus.Expired))
            {
                Status = RentalOrderStatus.Expired;
                return;
            }
            if (_items.All(i => i.Status == RentalItemStatus.Cancelled))
            {
                Status = RentalOrderStatus.Cancelled;
                return;
            }
        }

        // Priority after payment: InRental -> PendingSettlement -> AwaitingHandover -> PendingSupplierConfirmation
        if (_items.Any(i => i.Status == RentalItemStatus.InRental))
        {
            Status = RentalOrderStatus.InRental;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.PendingSettlement))
        {
            Status = RentalOrderStatus.PendingSettlement;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.AwaitingHandover))
        {
            Status = RentalOrderStatus.AwaitingHandover;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.PendingSupplierConfirmation))
        {
            Status = RentalOrderStatus.PendingSupplierConfirmation;
            return;
        }

        // Check if all items are terminal
        bool allTerminal = _items.All(i => i.Status is RentalItemStatus.Completed
            or RentalItemStatus.Rejected
            or RentalItemStatus.Expired
            or RentalItemStatus.Cancelled);

        if (allTerminal)
        {
            if (_items.Any(i => i.Status == RentalItemStatus.Completed))
            {
                Status = RentalOrderStatus.Completed;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Rejected))
            {
                Status = RentalOrderStatus.Rejected;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Expired))
            {
                Status = RentalOrderStatus.Expired;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Cancelled))
            {
                Status = RentalOrderStatus.Cancelled;
                return;
            }

            Status = RentalOrderStatus.Cancelled;
        }
    }

    private void LogTransition(
        Guid? rentalItemId,
        string? fromState,
        string toState,
        string action,
        Guid? actorId,
        ActorRole actorRole,
        string? reason,
        DateTimeOffset occurredAt,
        string operationId)
    {
        RentalStateTransition transition = new(
            Guid.NewGuid(),
            Id,
            rentalItemId,
            fromState,
            toState,
            action,
            actorId,
            actorRole,
            reason,
            occurredAt,
            operationId);

        _stateTransitions.Add(transition);
    }
}
