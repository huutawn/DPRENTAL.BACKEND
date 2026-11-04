namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO tổng hợp điểm tín nhiệm riêng tư của người thuê (GET /api/v1/reviews/my/reputation).
/// Chỉ người thuê là chủ sở hữu hoặc Admin mới có quyền truy cập; không công khai ra bên ngoài.
/// </summary>
public sealed record RenterReputationResponse(
    Guid RenterId,
    double? AverageRating,
    int ReviewCount,
    IReadOnlyDictionary<string, int> RatingDistribution);
