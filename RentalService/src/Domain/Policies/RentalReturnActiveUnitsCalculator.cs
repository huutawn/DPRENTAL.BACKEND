using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.Policies;

public static class RentalReturnActiveUnitsCalculator
{
    public static int Calculate(
        int totalQuantity,
        IEnumerable<Guid> allAssignmentIds,
        IEnumerable<Guid> returnedAssignmentIds,
        IEnumerable<Guid> activeReturnRequestedAssignmentIds)
    {
        var returnedSet = returnedAssignmentIds.ToHashSet();
        var requestedSet = activeReturnRequestedAssignmentIds.ToHashSet();

        int count = 0;
        foreach (var id in allAssignmentIds)
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

        var activeRequested = item.ReturnRequests
            .SelectMany(r => r.Units)
            .Where(u => u.ClosedAt == null && u.RejectedAt == null)
            .Select(u => u.RentalUnitAssignmentId)
            .ToHashSet();

        return item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue && !activeRequested.Contains(a.Id));
    }
}
