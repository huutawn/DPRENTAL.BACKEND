using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

public static class ReturnRequestStatusPolicy
{
    public static bool IsActionable(ReturnRequestUnit unit) => unit.IsActionable;

    public static HashSet<Guid> GetDisputedAssignmentIds(ReturnRequest request)
    {
        return request.Issues
            .SelectMany(i => i.Units.Select(u => u.RentalUnitAssignmentId))
            .ToHashSet();
    }

    public static ReturnRequestUnitStatus GetUnitStatus(
        ReturnRequestUnit unit,
        IReadOnlySet<Guid> disputedAssignmentIds)
    {
        if (unit.ClosedAt.HasValue) return ReturnRequestUnitStatus.Confirmed;
        if (unit.RejectedAt.HasValue) return ReturnRequestUnitStatus.Rejected;
        if (disputedAssignmentIds.Contains(unit.RentalUnitAssignmentId)) return ReturnRequestUnitStatus.Disputed;
        return ReturnRequestUnitStatus.PendingSupplierConfirmation;
    }

    public static ReturnRequestStatus GetRequestStatus(
        IReadOnlyCollection<ReturnRequestUnitStatus> unitStatuses)
    {
        if (unitStatuses.Count == 0) return ReturnRequestStatus.PendingSupplierConfirmation;

        bool hasActionable = unitStatuses.Any(s =>
            s is ReturnRequestUnitStatus.PendingSupplierConfirmation or ReturnRequestUnitStatus.Disputed);

        if (hasActionable)
        {
            return unitStatuses.Any(s => s == ReturnRequestUnitStatus.Disputed)
                ? ReturnRequestStatus.Disputed
                : ReturnRequestStatus.PendingSupplierConfirmation;
        }

        bool allConfirmed = unitStatuses.All(s => s == ReturnRequestUnitStatus.Confirmed);
        if (allConfirmed) return ReturnRequestStatus.Confirmed;

        bool allRejected = unitStatuses.All(s => s == ReturnRequestUnitStatus.Rejected);
        if (allRejected) return ReturnRequestStatus.Rejected;

        return ReturnRequestStatus.Resolved;
    }

    public static bool IsUnitOverdue(ReturnRequestUnit unit, DateTimeOffset deadline, DateTimeOffset now) =>
        unit.IsActionable && now >= deadline;

    public static bool IsRequestOverdue(ReturnRequest request, DateTimeOffset now) =>
        request.Units.Any(u => u.IsActionable) && now >= request.ConfirmationDeadlineAt;
}
