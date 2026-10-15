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
    string? Notes = null);

public sealed record ExtendHoldCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset NewHoldExpiresAt);

public sealed record ConfirmAllocationCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey);

public sealed record ReleaseAllocationCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] string Reason);

public sealed record MarkAssetsHandedOverCommandPayload(
    [property: JsonRequired] Guid RentalOrderId,
    [property: JsonRequired] Guid RentalItemId,
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] DateTimeOffset HandedOverAt);


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
