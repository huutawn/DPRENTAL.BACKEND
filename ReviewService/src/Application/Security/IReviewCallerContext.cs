using Dprental.ReviewService.Application.Services;

namespace Dprental.ReviewService.Application.Security;

/// <summary>
/// Hợp đồng ngữ cảnh người gọi cho ReviewService trích xuất từ JWT token.
/// </summary>
public interface IReviewCallerContext
{
    /// <summary>
    /// Cho biết yêu cầu đã được xác thực thành công hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Mã định danh người dùng (claim sub dạng UUID).
    /// </summary>
    Guid? ActorId { get; }

    /// <summary>
    /// Loại token/người dùng (claim u, ví dụ 'svc' cho service token).
    /// </summary>
    string? UserType { get; }

    /// <summary>
    /// Cờ xác định vai trò quản trị viên.
    /// </summary>
    bool IsAdmin { get; }

    /// <summary>
    /// Chuyển đổi sang ReviewActorContext cho tầng Application.
    /// </summary>
    ReviewActorContext ToActorContext();
}
