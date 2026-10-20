namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Tiêu chí tìm kiếm và phân trang tài sản trong phạm vi người sở hữu (OwnerUserId bắt buộc).
/// </summary>
public sealed record OwnerAssetSearchCriteria
{
    public Guid OwnerUserId { get; init; }
    public AssetSearchFilters Filters { get; init; }
    public bool? AcceptingBookings { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }

    public OwnerAssetSearchCriteria(
        Guid ownerUserId,
        AssetSearchFilters? filters = null,
        bool? acceptingBookings = null,
        int skip = 0,
        int take = 20)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(ownerUserId));
        }

        OwnerUserId = ownerUserId;
        Filters = filters ?? new AssetSearchFilters();
        AcceptingBookings = acceptingBookings;
        Skip = skip;
        Take = take;
    }

    public static OwnerAssetSearchCriteria ForOwner(Guid ownerUserId, int skip, int take) =>
        new(ownerUserId, new AssetSearchFilters(), skip: skip, take: take);
}
