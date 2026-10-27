using Dprental.RentalService.Application.Hubs;

namespace Dprental.RentalService.Application.DTOs.Inventory;

/// <summary>
/// Metadata invalidation đi kèm kết quả xử lý allocation event từ Inventory.
/// </summary>
public sealed record AllocationNotificationMetadata(
    RenterOrderChangedNotification? RenterNotification,
    IReadOnlyList<(Guid SupplierId, SupplierSubOrderChangedNotification Notification)> SupplierNotifications)
{
    public static readonly AllocationNotificationMetadata Empty = new(
        null,
        Array.Empty<(Guid, SupplierSubOrderChangedNotification)>());
}

/// <summary>
/// Kết quả xử lý allocation event từ Inventory bao gồm cờ thay đổi và metadata invalidation.
/// </summary>
public sealed record AllocationEventApplyResult(
    bool Changed,
    AllocationNotificationMetadata Metadata)
{
    public static AllocationEventApplyResult NoChange() =>
        new(false, AllocationNotificationMetadata.Empty);

    public static AllocationEventApplyResult WithMetadata(AllocationNotificationMetadata metadata) =>
        new(true, metadata);
}
