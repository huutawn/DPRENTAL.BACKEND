using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalOrder
{
    public Result SubmitHandover(
        Guid itemId,
        Guid supplierId,
        DateTimeOffset actualHandoverAt,
        IEnumerable<HandoverObservationDto> observations,
        string? note,
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

        var obsList = observations?.ToList() ?? [];
        string fingerprint = ComputeFingerprint("SubmitHandover", itemId, ActorRole.Supplier, supplierId, actualHandoverAt.ToUnixTimeMilliseconds(), note, obsList);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SubmitHandover", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SubmitHandover(Guid.NewGuid(), supplierId, actualHandoverAt, obsList, note?.Trim(), utcNow);
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
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SubmitHandover", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }

    public Result AcknowledgeHandover(
        Guid itemId,
        Guid renterId,
        DateTimeOffset acknowledgedAt,
        string? note,
        DateTimeOffset utcNow,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        string fingerprint = ComputeFingerprint("AcknowledgeHandover", itemId, ActorRole.Renter, renterId, acknowledgedAt.ToUnixTimeMilliseconds(), note);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "AcknowledgeHandover", fingerprint);
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

        Result result = item.AcknowledgeHandover(renterId, RenterId, acknowledgedAt, note?.Trim());
        if (result.IsFailure)
        {
            return result;
        }

        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "AcknowledgeHandover", fingerprint, utcNow);
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
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return RentalErrors.InvalidSnapshot;
        }

        RentalItem? item = _items.Find(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var obsList = observations?.ToList() ?? [];
        string fingerprint = ComputeFingerprint("SubmitReturn", itemId, ActorRole.Supplier, supplierId, actualReturnAt.ToUnixTimeMilliseconds(), note, obsList);
        Result? existingResult = CheckAndGetExistingReceipt(operationId.Trim(), "SubmitReturn", fingerprint);
        if (existingResult is not null)
        {
            return existingResult;
        }

        string itemFrom = item.Status.ToString();
        string orderFrom = Status.ToString();

        Result result = item.SubmitReturn(Guid.NewGuid(), supplierId, actualReturnAt, obsList, note?.Trim(), utcNow);
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
            operationId.Trim());

        RecalculateStatus();
        UpdatedAt = utcNow;
        Version++;

        RecordSuccessReceipt(operationId.Trim(), "SubmitReturn", fingerprint, utcNow);

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
                operationId.Trim());
        }

        return Result.Success();
    }
}
