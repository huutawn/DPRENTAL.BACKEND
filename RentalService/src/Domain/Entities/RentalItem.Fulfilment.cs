using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    internal Result SubmitHandover(
        Guid handoverRecordId,
        Guid supplierId,
        DateTimeOffset actualHandoverAt,
        IEnumerable<HandoverObservationDto> observations,
        string? note,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.InvalidHandoverActor;
        }

        if (HandoverRecord is not null)
        {
            return RentalErrors.HandoverAlreadySubmitted;
        }

        var obsList = observations?.ToList() ?? [];
        if (obsList.Count != _unitAssignments.Count)
        {
            return RentalErrors.IncompleteHandover;
        }

        if (obsList.Select(o => o.AssignmentId).Distinct().Count() != obsList.Count)
        {
            return RentalErrors.DuplicateAssignment;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var obs in obsList)
        {
            if (!assignDict.TryGetValue(obs.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.Assigned)
            {
                return RentalErrors.InvalidAssignmentStatus;
            }
        }

        HandoverRecord = new HandoverRecord(
            handoverRecordId,
            Id,
            actualHandoverAt,
            note,
            supplierId,
            utcNow,
            utcNow);

        foreach (var obs in obsList)
        {
            RentalUnitAssignment unit = assignDict[obs.AssignmentId];
            unit.RecordHandover(handoverRecordId, obs.Condition, obs.Note, obs.MediaIds);
            HandoverRecord.AddAssignment(unit);
        }

        Status = RentalItemStatus.InRental;
        return Result.Success();
    }

    internal Result AcknowledgeHandover(Guid renterId, Guid orderRenterId, DateTimeOffset acknowledgedAt, string? note)
    {
        if (renterId != orderRenterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (HandoverRecord is null)
        {
            return RentalErrors.InvalidItemStatus;
        }

        HandoverRecord.Acknowledge(renterId, acknowledgedAt, note);
        return Result.Success();
    }

    internal Result SubmitReturn(
        Guid returnRecordId,
        Guid supplierId,
        DateTimeOffset actualReturnAt,
        IEnumerable<ReturnObservationDto> observations,
        string? note,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.InRental)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.InvalidReturnActor;
        }

        var obsList = observations?.ToList() ?? [];
        if (obsList.Count == 0)
        {
            return RentalErrors.IncompleteReturn;
        }

        if (obsList.Select(o => o.AssignmentId).Distinct().Count() != obsList.Count)
        {
            return RentalErrors.DuplicateAssignment;
        }

        if (HandoverRecord is not null && actualReturnAt < HandoverRecord.ActualHandoverAt)
        {
            return RentalErrors.InvalidReturnTime;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var obs in obsList)
        {
            if (!assignDict.TryGetValue(obs.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.HandedOver)
            {
                return RentalErrors.UnitNotHandedOver;
            }
        }

        ReturnRecord returnRecord = new(
            returnRecordId,
            Id,
            actualReturnAt,
            note,
            supplierId,
            utcNow);

        _returnRecords.Add(returnRecord);

        foreach (var obs in obsList)
        {
            RentalUnitAssignment unit = assignDict[obs.AssignmentId];
            unit.RecordReturn(returnRecordId, obs.Condition, obs.Note, obs.MediaIds, null, null);
            returnRecord.AddAssignment(unit);
        }

        if (AreAllUnitsReturned)
        {
            ReturnsCompletedAt = utcNow;
            if (HasDamagedUnits)
            {
                Status = RentalItemStatus.PendingSettlement;
                DeductionDeadline = utcNow.AddHours(48);
            }
            else
            {
                FinalDeductionAmount = 0m;
            }
        }

        return Result.Success();
    }
}
