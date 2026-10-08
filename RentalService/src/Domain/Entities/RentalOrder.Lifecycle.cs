using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalOrder
{
    public Result ConfirmPayment(
        string invoiceRef,
        string paymentRef,
        DateTimeOffset confirmedAt,
        TimeSpan supplierConfirmationTimeout,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef) || string.IsNullOrWhiteSpace(paymentRef) || string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("ConfirmPayment", null, ActorRole.System, null, invoiceRef.Trim(), paymentRef.Trim(), confirmedAt.ToUnixTimeMilliseconds(), supplierConfirmationTimeout.Ticks);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "ConfirmPayment", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        if (Status is RentalOrderStatus.Expired or RentalOrderStatus.Cancelled)
        {
            LatePaymentRefundRequired = true;
            RecordSuccessReceipt(operationId.Trim(), "ConfirmPayment", fingerprint, confirmedAt);
            LogTransition(
                null,
                Status.ToString(),
                Status.ToString(),
                "LatePaymentReceived",
                null,
                ActorRole.System,
                "Payment received after order was terminal; refund requested",
                confirmedAt,
                operationId.Trim());
            return Result.Success();
        }

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
            Result itemResult = item.ConfirmPayment(supplierDeadline);
            if (itemResult.IsFailure)
            {
                return itemResult;
            }

            LogTransition(
                item.Id,
                itemFromState,
                item.Status.ToString(),
                "InvoicePaid",
                null,
                ActorRole.System,
                "Payment confirmed",
                confirmedAt,
                operationId.Trim());
        }

        RecalculateStatus();
        UpdatedAt = confirmedAt;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "ConfirmPayment", fingerprint, confirmedAt);

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "InvoicePaid",
            null,
            ActorRole.System,
            "Order payment confirmed",
            confirmedAt,
            operationId.Trim());

        return Result.Success();
    }

    public Result ExpirePayment(DateTimeOffset utcNow, string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("ExpirePayment", null, ActorRole.System, null);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "ExpirePayment", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        if (Status != RentalOrderStatus.PendingPayment)
        {
            return RentalErrors.InvalidOrderStatus;
        }

        if (utcNow < PaymentDeadline)
        {
            return RentalErrors.PaymentDeadlineNotReached;
        }

        string fromState = Status.ToString();
        foreach (var item in _items)
        {
            string itemFrom = item.Status.ToString();
            Result itemResult = item.ExpirePayment(utcNow);
            if (itemResult.IsFailure)
            {
                return itemResult;
            }

            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "PaymentTimeout",
                null,
                ActorRole.System,
                "Payment deadline expired",
                utcNow,
                operationId.Trim());
        }

        Status = RentalOrderStatus.Expired;
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "ExpirePayment", fingerprint, utcNow);

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "PaymentTimeout",
            null,
            ActorRole.System,
            "Order payment expired",
            utcNow,
            operationId.Trim());

        return Result.Success();
    }

    public Result CancelByRenter(Guid renterId, string reason, DateTimeOffset utcNow, string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("CancelByRenter", null, ActorRole.Renter, renterId, reason.Trim());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "CancelByRenter", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

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
            Result itemResult = item.CancelByRenter(reason.Trim(), utcNow);
            if (itemResult.IsFailure)
            {
                return itemResult;
            }

            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "RenterCancel",
                renterId,
                ActorRole.Renter,
                reason.Trim(),
                utcNow,
                operationId.Trim());
        }

        Status = RentalOrderStatus.Cancelled;
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "CancelByRenter", fingerprint, utcNow);

        LogTransition(
            null,
            fromState,
            Status.ToString(),
            "RenterCancel",
            renterId,
            ActorRole.Renter,
            reason.Trim(),
            utcNow,
            operationId.Trim());

        return Result.Success();
    }

    public Result SupplierAcceptItem(
        Guid itemId,
        Guid supplierId,
        string inventoryConfirmationRef,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(inventoryConfirmationRef))
        {
            return RentalErrors.InventoryConfirmationRequired;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("SupplierAccept", itemId, ActorRole.Supplier, supplierId, inventoryConfirmationRef.Trim());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SupplierAccept", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SupplierAccept(supplierId, inventoryConfirmationRef.Trim(), utcNow);
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
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SupplierAccept", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }

    public Result SupplierAcceptItem(Guid itemId, Guid supplierId, DateTimeOffset utcNow, string operationId) =>
        SupplierAcceptItem(itemId, supplierId, $"inv-conf-{operationId}", utcNow, operationId);

    public Result SupplierRejectItem(
        Guid itemId,
        Guid supplierId,
        string reason,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("SupplierReject", itemId, ActorRole.Supplier, supplierId, reason.Trim());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SupplierReject", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SupplierReject(supplierId, reason.Trim(), utcNow);
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
            reason.Trim(),
            utcNow,
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SupplierReject", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }

    public Result SupplierTimeoutItem(Guid itemId, DateTimeOffset utcNow, string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("SupplierTimeout", itemId, ActorRole.System, null);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SupplierTimeout", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
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
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SupplierTimeout", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }

    public Result CancelItemBeforeHandover(
        Guid itemId,
        Guid actorId,
        ActorRole actorRole,
        string reason,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("CancelBeforeHandover", itemId, actorRole, actorId, reason.Trim());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "CancelBeforeHandover", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.CancelBeforeHandover(actorId, actorRole, RenterId, reason.Trim(), utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        LogTransition(
            item.Id,
            itemFrom,
            item.Status.ToString(),
            "CancelBeforeHandover",
            actorId,
            actorRole,
            reason.Trim(),
            utcNow,
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "CancelBeforeHandover", fingerprint, utcNow);

        if (Status.ToString() != orderFrom)
        {
            LogTransition(
                null,
                orderFrom,
                Status.ToString(),
                "CancelBeforeHandover",
                actorId,
                actorRole,
                "Order status updated after item cancellation",
                utcNow,
                operationId.Trim());
        }

        return Result.Success();
    }

    public Result CancelItemBeforeHandover(Guid itemId, string reason, DateTimeOffset utcNow, string operationId) =>
        CancelItemBeforeHandover(itemId, RenterId, ActorRole.Renter, reason, utcNow, operationId);
}
