using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.Kafka.Contracts;

public sealed record ApplyReturnedUnitPayload(
    [Required] Guid AllocationId,
    [Required] string Condition);

public sealed record ApplyReturnedUnitsCommandPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required] string AllocationGroupId,
    [Required] Guid ReturnRecordId,
    [Required] DateTimeOffset ActualReturnAt,
    [Required] string OperationKey,
    [Required] IReadOnlyList<ApplyReturnedUnitPayload> Units);

public sealed record AppliedReturnedUnitPayload(
    [Required] Guid AllocationId,
    [Required] Guid AssetId,
    [Required] string Status,
    [Required] DateTimeOffset AllocationClosedAt,
    string? DamageDispositionRef);

public sealed record ReturnedUnitsAppliedPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required] string AllocationGroupId,
    [Required] Guid ReturnRecordId,
    [Required] string OperationKey,
    [Required] string ReceiptRef,
    [Required] DateTimeOffset AppliedAt,
    [Required] IReadOnlyList<AppliedReturnedUnitPayload> Units);

public sealed record ReturnedUnitsRejectedPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required] string AllocationGroupId,
    [Required] Guid ReturnRecordId,
    [Required] string OperationKey,
    [Required] string ErrorCode,
    [Required] string ErrorMessage);
