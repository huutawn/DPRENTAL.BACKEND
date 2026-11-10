using Dprental.RentalService.Application.DTOs.Orders;

namespace Dprental.RentalService.Application.DTOs.Reviews;

/// <summary>
/// DTO gửi lên ReviewService để kiểm tra trạng thái đánh giá theo lô (POST /api/v1/reviews/status/batch).
/// </summary>
public sealed record BatchReviewStatusRequest(
    IReadOnlyList<ReviewStatusItemRequest> Items);

/// <summary>
/// Phần tử yêu cầu trạng thái đánh giá gồm cặp orderId và itemId.
/// </summary>
public sealed record ReviewStatusItemRequest(
    Guid OrderId,
    Guid ItemId);

/// <summary>
/// DTO nhận về từ ReviewService chứa danh sách trạng thái đánh giá.
/// </summary>
public sealed record BatchReviewStatusResponse(
    IReadOnlyList<ReviewItemStatusResponse> Items);

/// <summary>
/// Chi tiết trạng thái đánh giá nhận từ ReviewService cho một mục thuê.
/// </summary>
public sealed record ReviewItemStatusResponse(
    Guid OrderId,
    Guid ItemId,
    ReviewStatusDto ReviewStatus);
