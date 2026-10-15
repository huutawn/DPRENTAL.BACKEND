namespace Dprental.RentalService.Application.DTOs.Integration;

/// <summary>Return evidence persisted; does not imply Inventory or Payment closure.</summary>
public sealed record RentalUnitsReturnedPayload(Guid OrderId, Guid RentalItemId, Guid ReturnRecordId,
    DateTimeOffset ActualReturnAt, DateTimeOffset SubmittedAt, IReadOnlyList<Guid> AssignmentIds);
