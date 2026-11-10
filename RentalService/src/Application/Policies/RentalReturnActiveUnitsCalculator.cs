using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

public static class RentalReturnActiveUnitsCalculator
{
    public static int Calculate(
        IEnumerable<Guid> allAssignmentIds,
        IEnumerable<Guid> returnedAssignmentIds,
        IEnumerable<Guid> activeReturnRequestedAssignmentIds)
    {
        ArgumentNullException.ThrowIfNull(allAssignmentIds);
        ArgumentNullException.ThrowIfNull(returnedAssignmentIds);
        ArgumentNullException.ThrowIfNull(activeReturnRequestedAssignmentIds);

        var distinctAssignments = allAssignmentIds.Distinct().ToList();
        if (distinctAssignments.Count == 0)
        {
            throw new InvalidOperationException("Item InRental thiếu assignment (danh sách assignment rỗng) là dữ liệu không hợp lệ.");
        }

        var returnedSet = returnedAssignmentIds.ToHashSet();
        var requestedSet = activeReturnRequestedAssignmentIds.ToHashSet();

        int count = 0;
        foreach (var id in distinctAssignments)
        {
            if (!returnedSet.Contains(id) && !requestedSet.Contains(id))
            {
                count++;
            }
        }
        return count;
    }

    public static int Calculate(RentalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var allAssignmentIds = item.UnitAssignments.Select(u => u.Id).ToList();
        var returnedAssignmentIds = item.UnitAssignments
            .Where(u => u.ReturnRecordId.HasValue)
            .Select(u => u.Id)
            .ToList();

        var activeReturnRequestedAssignmentIds = item.ReturnRequests
            .SelectMany(r => r.Units)
            .Where(u => u.ClosedAt == null && u.RejectedAt == null)
            .Select(u => u.RentalUnitAssignmentId)
            .ToList();

        return Calculate(allAssignmentIds, returnedAssignmentIds, activeReturnRequestedAssignmentIds);
    }
}
