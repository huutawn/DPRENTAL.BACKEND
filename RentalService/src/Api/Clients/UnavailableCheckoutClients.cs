#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Clients;

/// <summary>
/// Bản tạm của <see cref="IInventoryListingClient"/>: hợp đồng v1 với Inventory (ListingRentalSnapshotDto) chưa có
/// loại máy, hãng, model, thông số và ảnh mà checkout phải lưu, nên chưa có adapter thật. Luôn báo dịch vụ
/// chưa sẵn sàng để checkout trả 503 thay vì tạo đơn thiếu dữ liệu. Thay bằng adapter thật khi hợp đồng được mở rộng.
/// </summary>
public sealed class UnavailableInventoryListingClient(ILogger<UnavailableInventoryListingClient> logger) : IInventoryListingClient
{
    private static readonly Error ContractPending = Error.ServiceUnavailable(
        "Inventory.ListingContractPending", "Chưa có hợp đồng đọc Listing đầy đủ cho checkout.");

    public Task<Result<InventoryListingDto?>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Checkout chưa đọc được Listing {ListingId}: chưa có adapter Inventory cho checkout.", listingId);
        return Task.FromResult(Result.Failure<InventoryListingDto?>(ContractPending));
    }

    public Task<Result<InventoryListingAvailabilityDto>> CheckAvailabilityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<InventoryListingAvailabilityDto>(ContractPending));
}

/// <summary>
/// Bản tạm của <see cref="ICatalogReferenceClient"/>: Rental chưa có hợp đồng nào với CatalogService.
/// Luôn báo dịch vụ chưa sẵn sàng. Thay bằng adapter thật khi có hợp đồng.
/// </summary>
public sealed class UnavailableCatalogReferenceClient : ICatalogReferenceClient
{
    private static readonly Error ContractPending = Error.ServiceUnavailable(
        "Catalog.ReferenceContractPending", "Chưa có hợp đồng tra tên loại thiết bị và hãng cho checkout.");

    public Task<Result<string?>> GetEquipmentTypeNameAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string?>(ContractPending));

    public Task<Result<string?>> GetBrandNameAsync(Guid brandId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string?>(ContractPending));
}
