using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    /// <summary>
    /// Ghi nhận bàn giao sau khi Inventory đã xác nhận: tạo bản ghi bàn giao bất biến, gắn bằng chứng cho từng máy
    /// và chuyển dòng sang IN_RENTAL. Gọi lần hai khi dòng đã IN_RENTAL có bản ghi bàn giao thì không làm gì thêm.
    /// </summary>
    internal Result<HandoverRecord> CompleteHandover(
        Guid submittedById,
        DateTimeOffset submittedAt,
        DateTimeOffset actualHandoverAt,
        string? note,
        IReadOnlyList<HandoverUnitData> units,
        DateTimeOffset utcNow,
        string operationKey)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (HandoverRecord is not null && Status == RentalItemStatus.InRental)
        {
            return HandoverRecord;
        }

        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (HandoverRecord is not null ||
            units.Count != _unitAssignments.Count ||
            units.Select(u => u.AssignmentId).Distinct().Count() != units.Count ||
            units.Any(u => _unitAssignments.All(a => a.Id != u.AssignmentId) || u.MediaIds.Count == 0))
        {
            return Error.Validation("INVALID_UNITS", "Bằng chứng bàn giao phải đủ và duy nhất cho các máy thuộc mục thuê.");
        }

        var record = new HandoverRecord(
            Guid.NewGuid(), Id, actualHandoverAt, note, submittedById, submittedAt, submittedAt);
        HandoverRecord = record;

        foreach (HandoverUnitData unit in units)
        {
            RentalUnitAssignment assignment = _unitAssignments.First(a => a.Id == unit.AssignmentId);
            assignment.RecordHandover(record.Id, unit.Condition, unit.Note, unit.MediaIds);
            record.AddAssignment(assignment);
        }

        ApplyHandover(utcNow, operationKey);
        return record;
    }

    /// <summary>
    /// Người thuê xác nhận đã nhận thiết bị ngoài công trường: ghi nhận vào HandoverRecord và ghi 1 transition.
    /// Nếu đã xác nhận trước đó thì không ghi đè và không ghi thêm transition (idempotent).
    /// </summary>
    internal Result<HandoverRecord> AcknowledgeReceipt(
        Guid renterId,
        DateTimeOffset utcNow,
        string? note,
        string operationKey)
    {
        if (HandoverRecord is null)
        {
            return Error.Conflict("HANDOVER_NOT_RECORDED", "Mục thuê chưa có biên bản bàn giao từ nhà cung cấp.");
        }

        bool newlyAcknowledged = HandoverRecord.Acknowledge(renterId, utcNow, note);
        if (!newlyAcknowledged)
        {
            return HandoverRecord;
        }

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            Status.ToString(),
            Status.ToString(),
            "RenterAcknowledgeReceipt",
            renterId,
            ActorRole.Renter,
            "Renter acknowledged receipt of equipment",
            utcNow,
            operationKey));

        return HandoverRecord;
    }
}
