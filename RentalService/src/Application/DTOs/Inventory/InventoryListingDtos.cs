namespace Dprental.RentalService.Application.DTOs.Inventory;

/// <summary>
/// Thông tin Listing mà Rental cần để validate và chụp snapshot lúc checkout.
/// </summary>
public sealed record InventoryListingDto(
    Guid Id,
    Guid OwnerUserId,
    bool IsActive,
    string Title,
    Guid EquipmentTypeId,
    Guid BrandId,
    string Model,
    int SchemaVersion,
    string SpecValuesJson,
    Guid? ThumbnailMediaId,
    decimal? DailyRate,
    decimal? DepositPerUnit,
    string? Currency,
    string? PickupLocation,
    string? SnapshotVersion);

public sealed record InventoryListingAvailabilityDto(
    Guid ListingId,
    int AvailableQuantity,
    bool IsAvailable);
