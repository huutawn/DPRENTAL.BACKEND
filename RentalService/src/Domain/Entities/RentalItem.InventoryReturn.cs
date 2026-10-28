using Dprental.RentalService.Common;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    public const string MarkReturnedAction = "MarkAssetsReturned";
    public const string BlockDamagedAction = "BlockDamagedAssets";
    public const string ApplyReturnedUnitsAction = "ApplyReturnedUnits";

    internal void SetInventoryClosure(string receiptRef, DateTimeOffset confirmedAt)
    {
        InventoryClosureRef = receiptRef;
        InventoryClosureConfirmedAt = confirmedAt.ToUniversalTime();
    }

    internal Result ConfirmReturnedUnitClosure(Guid assignmentId, DateTimeOffset closedAt,
        string? dispositionRef, string receiptRef, DateTimeOffset confirmedAt)
    {
        var unit = _unitAssignments.FirstOrDefault(a => a.Id == assignmentId);
        if (unit is null) return Error.Validation("RETURN_ASSIGNMENT_NOT_FOUND", "Returned assignment was not found.");
        unit.RecordAllocationClosed(closedAt.ToUniversalTime(), dispositionRef);
        if (AreAllUnitsReturned && _unitAssignments.All(a => a.AllocationClosedAt.HasValue))
        {
            SetInventoryClosure(receiptRef, confirmedAt);
        }
        return Result.Success();
    }

    internal Result ConfirmDamagedUnitBlocked(Guid assignmentId, string dispositionRef)
    {
        var unit = _unitAssignments.FirstOrDefault(a => a.Id == assignmentId);
        if (unit is null) return Error.Validation("DAMAGED_ASSIGNMENT_NOT_FOUND", "Damaged assignment was not found.");
        unit.RecordInventoryDisposition(dispositionRef);
        return Result.Success();
    }
}
