using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    public Result<ReturnRecord> RecordAssetReturns(
        Guid supplierId, DateTimeOffset actualReturnAt, string? note,
        List<ReturnUnitData> unitsToReturn, DateTimeOffset submittedAt, Guid submittedById)
    {
        var validation = ValidateAssetReturns(supplierId, actualReturnAt, note, unitsToReturn, submittedAt, submittedById);
        if (validation.IsFailure) return validation.Error;
        var record = new ReturnRecord(Guid.NewGuid(), Id, actualReturnAt.ToUniversalTime(), note,
            submittedById, submittedAt.ToUniversalTime());
        _returnRecords.Add(record);
        foreach (var unit in unitsToReturn)
        {
            var assignment = _unitAssignments.First(a => a.Id == unit.AssignmentId);
            assignment.RecordReturn(record.Id, unit.Condition, unit.Description, unit.MediaIds, null, null);
            record.AddAssignment(assignment);
        }
        if (AreAllUnitsReturned)
        {
            ReturnsCompletedAt = submittedAt.ToUniversalTime();
            if (HasDamagedUnits)
            {
                Status = RentalItemStatus.PendingSettlement;
                DeductionDeadline = ReturnsCompletedAt.Value.AddHours(48);
            }
            else FinalDeductionAmount = 0m;
        }
        return record;
    }

    public Result ValidateAssetReturns(Guid supplierId, DateTimeOffset actualReturnAt, string? note,
        List<ReturnUnitData> unitsToReturn, DateTimeOffset submittedAt, Guid submittedById)
    {
        if (supplierId == Guid.Empty || SupplierId != supplierId || submittedById != supplierId)
            return RentalErrors.UnauthorizedSupplier;
        if (Status != RentalItemStatus.InRental) return RentalErrors.InvalidItemStatusForReturn;
        if (unitsToReturn is null || unitsToReturn.Count == 0 || unitsToReturn.Any(u => u is null) ||
            unitsToReturn.Select(u => u.AssignmentId).Distinct().Count() != unitsToReturn.Count)
            return Error.Validation("Return.InvalidUnits", "Specify distinct assignments to return.");
        if (note?.Length > 2000 || actualReturnAt == default)
            return Error.Validation("Return.InvalidRequest", "Invalid return time or note.");
        if (actualReturnAt > submittedAt) return RentalErrors.FutureReturnTimeNotAllowed;

        // Validate the whole request before modifying any assignment.
        foreach (var unit in unitsToReturn)
        {
            var assignment = _unitAssignments.Find(a => a.Id == unit.AssignmentId);
            if (assignment is null) return RentalErrors.AssignmentNotFound;
            if (assignment.ReturnRecordId.HasValue) return RentalErrors.AssetAlreadyReturned(unit.AssignmentId);
            if (assignment.Status != RentalUnitAssignmentStatus.HandedOver || HandoverRecord is null ||
                assignment.HandoverRecordId != HandoverRecord.Id)
                return RentalErrors.AssetNotHandedOver(unit.AssignmentId);
            if (actualReturnAt < HandoverRecord.ActualHandoverAt)
                return RentalErrors.InvalidReturnTimeBeforeHandover(unit.AssignmentId);
            if (!Enum.IsDefined(unit.Condition) || unit.Description?.Length > 2000 || unit.MediaIds is null ||
                unit.MediaIds.Count > 10 || unit.MediaIds.Contains(Guid.Empty) ||
                unit.MediaIds.Distinct().Count() != unit.MediaIds.Count)
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");
        }
        return Result.Success();
    }
}

public sealed record ReturnUnitData(Guid AssignmentId, UnitCondition Condition, string? Description, List<Guid> MediaIds);
