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
            if (HasDamagedUnits && DepositAmount > 0m)
            {
                Status = RentalItemStatus.PendingSettlement;
                DeductionDeadline = ReturnsCompletedAt.Value.AddHours(48);
            }
            else
            {
                FinalDeductionAmount = 0m;
                Status = RentalItemStatus.PendingSettlement;
            }
        }
        return record;
    }
}
