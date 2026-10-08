using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    internal Result ConfirmPayment(DateTimeOffset confirmationDeadline)
    {
        if (Status != RentalItemStatus.PendingPayment)
        {
            return RentalErrors.InvalidItemStatus;
        }

        Status = RentalItemStatus.PendingSupplierConfirmation;
        SupplierConfirmationDeadline = confirmationDeadline;
        return Result.Success();
    }

    internal Result ExpirePayment(DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingPayment)
        {
            return RentalErrors.InvalidItemStatus;
        }

        Status = RentalItemStatus.Expired;
        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result CancelByRenter(string reason, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingPayment)
        {
            return RentalErrors.InvalidItemStatus;
        }

        Status = RentalItemStatus.Cancelled;
        SupplierDecisionReason = reason;

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result SupplierAccept(Guid supplierId, string inventoryConfirmationRef, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (SupplierConfirmationDeadline.HasValue && utcNow >= SupplierConfirmationDeadline.Value)
        {
            return RentalErrors.SupplierDeadlineExpired;
        }

        if (string.IsNullOrWhiteSpace(inventoryConfirmationRef))
        {
            return RentalErrors.InventoryConfirmationRequired;
        }

        Status = RentalItemStatus.AwaitingHandover;
        SupplierDecisionAt = utcNow;
        InventoryConfirmationRef = inventoryConfirmationRef.Trim();
        return Result.Success();
    }

    internal Result SupplierReject(Guid supplierId, string reason, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (SupplierConfirmationDeadline.HasValue && utcNow >= SupplierConfirmationDeadline.Value)
        {
            return RentalErrors.SupplierDeadlineExpired;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        Status = RentalItemStatus.Rejected;
        SupplierDecisionAt = utcNow;
        SupplierDecisionReason = reason.Trim();

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result SupplierTimeout(DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierConfirmationDeadline.HasValue && utcNow < SupplierConfirmationDeadline.Value)
        {
            return RentalErrors.SupplierDeadlineNotExpired;
        }

        Status = RentalItemStatus.Expired;
        SupplierDecisionAt = utcNow;
        SupplierDecisionReason = "Supplier không xác nhận đúng hạn.";

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result CancelBeforeHandover(Guid actorId, ActorRole actorRole, Guid orderRenterId, string reason, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (actorRole == ActorRole.Renter && actorId != orderRenterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (actorRole == ActorRole.Supplier && actorId != SupplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (actorRole != ActorRole.Renter && actorRole != ActorRole.Supplier && actorRole != ActorRole.System)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        Status = RentalItemStatus.Cancelled;
        SupplierDecisionReason = reason.Trim();

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }
}
