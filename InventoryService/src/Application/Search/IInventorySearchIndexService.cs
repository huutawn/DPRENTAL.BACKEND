using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Giao diện tầng Application để truy vấn tập ứng viên từ Meilisearch.
/// Giữ độc lập hoàn toàn với Meilisearch SDK.
/// </summary>
public interface IInventorySearchIndexService
{
    /// <summary>
    /// Tìm kiếm tập ứng viên Listing công khai xếp hạng theo relevance.
    /// </summary>
    Task<Result<CandidateSearchResult>> SearchPublicListingCandidatesAsync(
        PublicListingSearchIndexQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm tập ứng viên Listing của chủ sở hữu xếp hạng theo relevance.
    /// </summary>
    Task<Result<CandidateSearchResult>> SearchOwnerListingCandidatesAsync(
        OwnerListingSearchIndexQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm tập ứng viên Tài sản vật lý của chủ sở hữu xếp hạng theo relevance.
    /// </summary>
    Task<Result<CandidateSearchResult>> SearchOwnerAssetCandidatesAsync(
        OwnerAssetSearchIndexQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra trạng thái sẵn sàng của dịch vụ chỉ mục tìm kiếm.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
