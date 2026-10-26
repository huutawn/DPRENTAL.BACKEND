namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Một dòng read model cho một dòng thuê (một thiết bị của một NCC trong đơn). Chỉ chứa dữ liệu.
/// </summary>
public sealed class RentalItemFact
{
    public Guid RentalItemId { get; set; }
    public Guid RentalOrderId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid EquipmentListingId { get; set; }
    public Guid? EquipmentTypeId { get; set; }
    public string? EquipmentTypeName { get; set; }
    public Guid? BrandId { get; set; }
    public string? BrandName { get; set; }
    public string? Model { get; set; }
    public int Quantity { get; set; }
    public decimal RentalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public string? ExpiryReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? HandoverAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Trạng thái hiện tại của dòng thuê và orderVersion của event cuối đã đổi nó (0 = chưa có version)
    public string Status { get; set; } = "UNKNOWN";
    public long StatusVersion { get; set; }
}
