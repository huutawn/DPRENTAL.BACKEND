using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

public sealed record HandoverObservationDto(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note = null,
    IEnumerable<Guid>? MediaIds = null);

public sealed record ReturnObservationDto(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note = null,
    IEnumerable<Guid>? MediaIds = null);

public sealed record DeductionLineDto(
    Guid AssignmentId,
    decimal ProposedAmount,
    string Reason,
    IEnumerable<Guid>? MediaIds = null);

public sealed record InitialRentalUnitAssignmentDto(
    Guid Id,
    Guid AssetId,
    Guid AllocationId);

public sealed record InitialRentalItemDto(
    Guid Id,
    Guid EquipmentListingId,
    Guid SupplierId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    string ListingTitleSnapshot,
    string? ListingDescriptionSnapshot,
    string PickupLocationSnapshot,
    decimal DailyRateSnapshot,
    decimal DepositPerUnitSnapshot,
    string AllocationGroupId,
    IEnumerable<InitialRentalUnitAssignmentDto> Assignments);

public sealed record RentalOperationCommand(
    int Version,
    string Action,
    Guid RentalOrderId,
    Guid? TargetItemId,
    string OperationKey,
    string? TargetGroupOrReference,
    Guid? ActorId,
    string? ProviderRequestJson);
