using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.Kafka.Contracts;

public sealed record CreateHoldCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] Guid EquipmentListingId,
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] DateOnly StartDate,
    [property: JsonRequired] DateOnly EndDate,
    [property: JsonRequired] DateTimeOffset HoldExpiresAt,
    string? Notes = null,
    string? AllocationGroupId = null,
    DateTimeOffset? ExecutionCutoffUtc = null);

public sealed record ExtendHoldCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset NewHoldExpiresAt,
    string? AllocationGroupId = null);

public sealed record ConfirmAllocationCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    string? AllocationGroupId = null);

public sealed record ReleaseAllocationCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string Reason,
    string? AllocationGroupId = null);

public sealed record MarkAssetsHandedOverCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset HandedOverAt,
    string? AllocationGroupId = null);
public sealed record MarkAssetsOverdueCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string AllocationGroupId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] IReadOnlyList<Guid> AllocationIds);


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
    DateTimeOffset? OccurredAt = null,
    string? AllocationGroupId = null);

public sealed record InventoryAllocationCommandRejectedPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string CommandType,
    [property: JsonRequired] string ErrorCode,
    [property: JsonRequired] string ErrorMessage,
    DateTimeOffset? OccurredAt = null,
    string? AllocationGroupId = null);
