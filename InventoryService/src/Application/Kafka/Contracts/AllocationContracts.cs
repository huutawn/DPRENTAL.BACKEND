using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Contracts;

public static class AllocationMessageTypes
{
    public const string CreateHold = "CreateHold";
    public const string ExtendHold = "ExtendHold";
    public const string ConfirmAllocation = "ConfirmAllocation";
    public const string ReleaseAllocation = "ReleaseAllocation";
    public const string MarkAssetsHandedOver = "MarkAssetsHandedOver";
    public const string ApplyReturnedUnits = "ApplyReturnedUnits";

    public const string Succeeded = "InventoryAllocationCommandSucceeded";
    public const string Rejected = "InventoryAllocationCommandRejected";

    public const string ReturnedUnitsApplied = "ReturnedUnitsApplied";
    public const string ReturnedUnitsRejected = "ReturnedUnitsRejected";
}

public static class AssetReturnConditions
{
    public const string Normal = "NORMAL";
    public const string Damaged = "DAMAGED";

    public static bool IsValid(string? condition) =>
        string.Equals(condition, Normal, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(condition, Damaged, StringComparison.OrdinalIgnoreCase);

    public static bool IsDamaged(string? condition) =>
        string.Equals(condition, Damaged, StringComparison.OrdinalIgnoreCase);
}

public sealed record CreateHoldPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] Guid EquipmentListingId,
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] DateOnly StartDate,
    [property: JsonRequired] DateOnly EndDate,
    [property: JsonRequired] DateTimeOffset HoldExpiresAt,
    string? Notes = null);

public sealed record ExtendHoldPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset NewHoldExpiresAt);

public sealed record ConfirmAllocationPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey);

public sealed record ReleaseAllocationPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string Reason);

public sealed record MarkAssetsHandedOverPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset HandedOverAt);

public sealed record ApplyReturnedUnitPayload(
    [property: JsonRequired] Guid AllocationId,
    [property: JsonRequired] string Condition);

public sealed record ApplyReturnedUnitsCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string AllocationGroupId,
    [property: JsonRequired] Guid ReturnRecordId,
    [property: JsonRequired] DateTimeOffset ActualReturnAt,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] IReadOnlyList<ApplyReturnedUnitPayload> Units);

public sealed record AppliedReturnedUnitPayload(
    [property: JsonRequired] Guid AllocationId,
    [property: JsonRequired] Guid AssetId,
    [property: JsonRequired] string Status,
    [property: JsonRequired] DateTimeOffset AllocationClosedAt,
    string? DamageDispositionRef = null);

public sealed record ReturnedUnitsAppliedPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string AllocationGroupId,
    [property: JsonRequired] Guid ReturnRecordId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string ReceiptRef,
    [property: JsonRequired] DateTimeOffset AppliedAt,
    [property: JsonRequired] IReadOnlyList<AppliedReturnedUnitPayload> Units);

public sealed record ReturnedUnitsRejectedPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string AllocationGroupId,
    [property: JsonRequired] Guid ReturnRecordId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string ErrorCode,
    [property: JsonRequired] string ErrorMessage);

public sealed record AllocationAssetMappingPayload(
    [property: JsonRequired] Guid AllocationId,
    [property: JsonRequired] Guid AssetId,
    [property: JsonRequired] string AssetCode);

public sealed record InventoryAllocationCommandSucceededPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string CommandType,
    [property: JsonRequired] string Status,
    DateTimeOffset? HoldExpiresAt = null,
    IReadOnlyList<AllocationAssetMappingPayload>? Allocations = null,
    DateTimeOffset? OccurredAt = null);

public sealed record InventoryAllocationCommandRejectedPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string CommandType,
    [property: JsonRequired] string ErrorCode,
    [property: JsonRequired] string ErrorMessage,
    DateTimeOffset? OccurredAt = null);
