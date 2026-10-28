using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public static class ReturnTestDataSeeder
{
    public static ReturnRecord SeedReturn(
        RentalItem item,
        Guid supplierId,
        DateTimeOffset actualReturnAt,
        string? note,
        IReadOnlyList<ReturnUnitData> units,
        DateTimeOffset submittedAt,
        Guid submittedById)
    {
        var record = new ReturnRecord(
            Guid.NewGuid(),
            item.Id,
            actualReturnAt.ToUniversalTime(),
            note,
            submittedById,
            submittedAt.ToUniversalTime());

        item.AddReturnRecord(record);

        foreach (var unit in units)
        {
            var assignment = item.UnitAssignments.First(a => a.Id == unit.AssignmentId);
            assignment.RecordReturn(record.Id, unit.Condition, unit.Description, unit.MediaIds, null, null);
            record.AddAssignment(assignment);
        }

        if (item.AreAllUnitsReturned)
        {
            var completedAt = submittedAt.ToUniversalTime();
            if (item.HasDamagedUnits && item.DepositAmount > 0m)
            {
                item.SetReturnsCompleted(completedAt, completedAt.AddHours(48), null);
            }
            else
            {
                item.SetReturnsCompleted(completedAt, null, 0m);
            }
        }

        return record;
    }
}
