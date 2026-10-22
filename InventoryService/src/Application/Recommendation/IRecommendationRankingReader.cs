namespace Dprental.InventoryService.Application.Recommendation;

/// <summary>
/// Giao diện đọc snapshot bảng xếp hạng recommendation từ bộ nhớ đệm phân tán.
/// </summary>
public interface IRecommendationRankingReader
{
    /// <summary>
    /// Đọc snapshot sở thích cá nhân (nếu là human) và độ phổ biến toàn cục.
    /// Giới hạn tối đa 20 interests và 300 popular listings.
    /// </summary>
    Task<RecommendationSnapshot> ReadAsync(Guid? userId, CancellationToken cancellationToken = default);
}
