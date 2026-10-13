namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Kết quả truy vấn tập ID ứng viên từ chỉ mục tìm kiếm.
/// </summary>
public sealed record CandidateSearchResult(
    IReadOnlyList<Guid> CandidateIds,
    int TotalHits,
    bool IsTruncated,
    string Backend = "meilisearch"
)
{
    public static readonly CandidateSearchResult Empty = new(Array.Empty<Guid>(), 0, false);
}
