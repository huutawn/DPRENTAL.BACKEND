using Dprental.RentalService.Common;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    public const string MarkReturnedAction = "MarkAssetsReturned";
    public const string BlockDamagedAction = "BlockDamagedAssets";

    public Result ConfirmReturnedUnitClosure(Guid assignmentId, DateTimeOffset closedAt,
        string? dispositionRef, string receiptRef, DateTimeOffset confirmedAt)
    {
        var unit = _unitAssignments.FirstOrDefault(a => a.Id == assignmentId);
        if (unit is null || unit.ReturnRecordId is null)
            return Error.Validation("RETURN_ASSIGNMENT_NOT_FOUND", "Returned assignment was not found.");
        if (closedAt == default || string.IsNullOrWhiteSpace(receiptRef))
            return Error.Validation("INVALID_INVENTORY_CLOSURE", "Inventory closure is incomplete.");
        if (unit.AllocationClosedAt.HasValue)
            return unit.AllocationClosedAt == closedAt && unit.InventoryDispositionRef == dispositionRef
                ? Result.Success()
                : Error.Conflict("INVENTORY_CLOSURE_CONFLICT", "Assignment closure differs from the recorded result.");

        unit.RecordAllocationClosed(closedAt.ToUniversalTime(), dispositionRef);
        if (AreAllUnitsReturned && _unitAssignments.All(a => a.AllocationClosedAt.HasValue))
        {
            InventoryClosureRef = receiptRef;
            InventoryClosureConfirmedAt = confirmedAt.ToUniversalTime();
        }
        return Result.Success();
    }

    public Result ConfirmDamagedUnitBlocked(Guid assignmentId, string dispositionRef)
    {
        var unit = _unitAssignments.FirstOrDefault(a => a.Id == assignmentId);
        if (unit is null || unit.Status != Common.Enums.RentalUnitAssignmentStatus.ReturnedDamaged)
            return Error.Validation("DAMAGED_ASSIGNMENT_NOT_FOUND", "Damaged assignment was not found.");
        if (string.IsNullOrWhiteSpace(dispositionRef))
            return Error.Validation("INVALID_DAMAGE_DISPOSITION", "Inventory damage protection is missing.");
        if (unit.InventoryDispositionRef is not null && unit.InventoryDispositionRef != dispositionRef)
            return Error.Conflict("DAMAGE_DISPOSITION_CONFLICT", "Damage disposition differs from the recorded result.");
        unit.RecordInventoryDisposition(dispositionRef);
        return Result.Success();
    }
}
