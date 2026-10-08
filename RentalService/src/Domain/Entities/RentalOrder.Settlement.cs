using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalOrder
{
    public Result SubmitDeductionProposal(
        Guid itemId,
        Guid supplierId,
        IEnumerable<DeductionLineDto> lines,
        DateTimeOffset utcNow,
        string operationId)
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

        var lineList = lines?.ToList() ?? [];
        string fingerprint = ComputeFingerprint("SubmitDeductionProposal", itemId, ActorRole.Supplier, supplierId, lineList);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SubmitDeductionProposal", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        Result result = item.SubmitDeductionProposal(Guid.NewGuid(), supplierId, lineList, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SubmitDeductionProposal", fingerprint, utcNow);
        return Result.Success();
    }

    public Result ExpireDeductionSubmission(Guid itemId, DateTimeOffset utcNow, string operationId)
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

        string fingerprint = ComputeFingerprint("ExpireDeductionSubmission", itemId, ActorRole.System, null);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "ExpireDeductionSubmission", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        Result result = item.ExpireDeductionSubmission(utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "ExpireDeductionSubmission", fingerprint, utcNow);
        return Result.Success();
    }

    public Result AcceptDeductionProposal(Guid itemId, Guid renterId, DateTimeOffset utcNow, string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("AcceptDeductionProposal", itemId, ActorRole.Renter, renterId);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "AcceptDeductionProposal", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.AcceptDeductionProposal(renterId, RenterId, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "AcceptDeductionProposal", fingerprint, utcNow);
        return Result.Success();
    }

    public Result TimeoutDeductionProposal(Guid itemId, DateTimeOffset utcNow, string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("TimeoutDeductionProposal", itemId, ActorRole.System, null);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "TimeoutDeductionProposal", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

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

        RecordSuccessReceipt(operationId.Trim(), "TimeoutDeductionProposal", fingerprint, utcNow);
        return Result.Success();
    }

    public Result DisputeDeductionProposal(
        Guid itemId,
        Guid renterId,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        var mediaList = mediaIds?.ToList() ?? [];
        string fingerprint = ComputeFingerprint("DisputeDeductionProposal", itemId, ActorRole.Renter, renterId, reason.Trim(), mediaList);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "DisputeDeductionProposal", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        if (RenterId != renterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.DisputeDeductionProposal(Guid.NewGuid(), renterId, RenterId, reason.Trim(), mediaList, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "DisputeDeductionProposal", fingerprint, utcNow);
        return Result.Success();
    }

    public Result ResolveDispute(
        Guid itemId,
        Guid adminId,
        ActorRole actorRole,
        decimal finalDeductionAmount,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        var mediaList = mediaIds?.ToList() ?? [];
        string fingerprint = ComputeFingerprint("ResolveDispute", itemId, actorRole, adminId, finalDeductionAmount, reason.Trim(), mediaList);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "ResolveDispute", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        if (actorRole != ActorRole.Admin)
        {
            return RentalErrors.AdminOnlyResolution;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        Result result = item.ResolveDispute(adminId, actorRole, finalDeductionAmount, reason.Trim(), mediaList, utcNow);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "ResolveDispute", fingerprint, utcNow);
        return Result.Success();
    }

    public Result ResolveDispute(
        Guid itemId,
        Guid adminId,
        decimal finalDeductionAmount,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow,
        string operationId) =>
        ResolveDispute(itemId, adminId, ActorRole.Admin, finalDeductionAmount, reason, mediaIds, utcNow, operationId);

    public Result RecordInventoryClosureReceipt(
        Guid itemId,
        string inventoryClosureRef,
        DateTimeOffset confirmedAt,
        string operationId,
        IEnumerable<Guid>? closedAssignmentIds = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(inventoryClosureRef))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("RecordInventoryClosureReceipt", itemId, ActorRole.System, null, inventoryClosureRef.Trim(), confirmedAt.ToUnixTimeMilliseconds());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "RecordInventoryClosureReceipt", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.RecordInventoryClosure(inventoryClosureRef.Trim(), confirmedAt, closedAssignmentIds);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = confirmedAt;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "RecordInventoryClosureReceipt", fingerprint, confirmedAt);

        if (item.Status.ToString() != itemFrom)
        {
            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "InventoryClosureConfirmed",
                null,
                ActorRole.System,
                $"Inventory closure receipt applied: {inventoryClosureRef.Trim()}",
                confirmedAt,
                operationId.Trim());

            RecalculateStatus();

            if (Status.ToString() != orderFrom)
            {
                LogTransition(
                    null,
                    orderFrom,
                    Status.ToString(),
                    "InventoryClosureConfirmed",
                    null,
                    ActorRole.System,
                    "Order status updated after inventory closure",
                    confirmedAt,
                    operationId.Trim());
            }
        }

        return Result.Success();
    }

    public Result RecordPaymentSettlementReceipt(
        Guid itemId,
        string settlementRef,
        string? refundRef,
        DateTimeOffset confirmedAt,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(settlementRef))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        string fingerprint = ComputeFingerprint("RecordPaymentSettlementReceipt", itemId, ActorRole.System, null, settlementRef.Trim(), refundRef?.Trim() ?? "null", confirmedAt.ToUnixTimeMilliseconds());
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "RecordPaymentSettlementReceipt", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.RecordPaymentSettlement(settlementRef.Trim(), refundRef?.Trim(), confirmedAt);
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = confirmedAt;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "RecordPaymentSettlementReceipt", fingerprint, confirmedAt);

        if (item.Status.ToString() != itemFrom)
        {
            LogTransition(
                item.Id,
                itemFrom,
                item.Status.ToString(),
                "PaymentSettlementConfirmed",
                null,
                ActorRole.System,
                $"Payment settlement receipt applied: {settlementRef.Trim()}",
                confirmedAt,
                operationId.Trim());

            RecalculateStatus();

            if (Status.ToString() != orderFrom)
            {
                LogTransition(
                    null,
                    orderFrom,
                    Status.ToString(),
                    "PaymentSettlementConfirmed",
                    null,
                    ActorRole.System,
                    "Order status updated after settlement",
                    confirmedAt,
                    operationId.Trim());
            }
        }

        return Result.Success();
    }

    public Result ConfirmClosuresAndComplete(
        Guid itemId,
        DateTimeOffset inventoryClosureAt,
        DateTimeOffset financialClosureAt,
        string? settlementRef,
        string? refundRef,
        DateTimeOffset utcNow,
        string operationId,
        string? inventoryClosureRef = null)
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

        string fingerprint = ComputeFingerprint("ConfirmClosuresAndComplete", itemId, ActorRole.System, null, inventoryClosureAt.ToUnixTimeMilliseconds(), financialClosureAt.ToUnixTimeMilliseconds(), settlementRef, refundRef, inventoryClosureRef);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "ConfirmClosuresAndComplete", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.ConfirmClosuresAndComplete(inventoryClosureAt, financialClosureAt, settlementRef, refundRef, inventoryClosureRef);
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
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "ConfirmClosuresAndComplete", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }
}
