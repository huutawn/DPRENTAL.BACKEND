using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Policies;

/// <summary>
/// Nguồn chân lý duy nhất cho điều kiện được phép tạo yêu cầu trả máy (eligibility),
/// dùng chung cho cả query mapping (CanRequestReturn) và command validation.
/// </summary>
public static class ReturnEligibilityPolicy
{
    public static bool IsActionable(ReturnRequestUnit unit) => unit.IsActionable;

    public static bool HasOpenReturnRequest(RentalItem item, Guid assignmentId)
    {
        return item.ReturnRequests
            .SelectMany(r => r.Units)
            .Any(u => u.RentalUnitAssignmentId == assignmentId && u.IsActionable);
    }

    public static HashSet<Guid> GetOpenReturnRequestAssignmentIds(RentalItem item)
    {
        return item.ReturnRequests
            .SelectMany(r => r.Units)
            .Where(u => u.IsActionable)
            .Select(u => u.RentalUnitAssignmentId)
            .ToHashSet();
    }

    public static bool CanRequestReturn(RentalItem item, RentalUnitAssignment assignment)
    {
        return CanRequestReturn(item, assignment, GetOpenReturnRequestAssignmentIds(item));
    }

    public static bool CanRequestReturn(RentalItem item, RentalUnitAssignment assignment, IReadOnlySet<Guid> openRequestAssignmentIds)
    {
        if (item.Status != RentalItemStatus.InRental) return false;
        if (assignment.ReturnRecordId.HasValue) return false;
        if (assignment.Status != RentalUnitAssignmentStatus.HandedOver) return false;
        if (item.HandoverRecord is null || assignment.HandoverRecordId != item.HandoverRecord.Id) return false;
        if (openRequestAssignmentIds.Contains(assignment.Id)) return false;
        return true;
    }

    public static Result ValidateAssignmentEligibleForReturn(
        RentalItem item,
        RentalUnitAssignment assignment,
        DateTimeOffset actualReturnAt)
    {
        if (item.Status != RentalItemStatus.InRental)
            return RentalErrors.InvalidItemStatusForReturn;
        if (assignment.ReturnRecordId.HasValue)
            return RentalErrors.AssetAlreadyReturned(assignment.Id);
        if (assignment.Status != RentalUnitAssignmentStatus.HandedOver || item.HandoverRecord is null ||
            assignment.HandoverRecordId != item.HandoverRecord.Id)
            return RentalErrors.AssetNotHandedOver(assignment.Id);
        if (actualReturnAt < item.HandoverRecord.ActualHandoverAt)
            return RentalErrors.InvalidReturnTimeBeforeHandover(assignment.Id);

        return Result.Success();
    }

    public static Result ValidateAssignmentForReturnRequest(
        RentalItem item,
        RentalUnitAssignment assignment,
        DateTimeOffset actualReturnAt,
        IReadOnlySet<Guid> openRequestAssignmentIds)
    {
        var eligible = ValidateAssignmentEligibleForReturn(item, assignment, actualReturnAt);
        if (eligible.IsFailure) return eligible.Error;

        if (openRequestAssignmentIds.Contains(assignment.Id))
            return Error.Conflict("RETURN_REQUEST_ALREADY_OPEN", "This unit already has an open return request.");

        return Result.Success();
    }
}
