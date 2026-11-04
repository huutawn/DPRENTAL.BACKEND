namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Ngữ cảnh người thực hiện thao tác trên đánh giá (trích xuất từ JWT token).
/// </summary>
/// <param name="ActorId">Mã định danh người dùng (claim sub dạng UUID).</param>
/// <param name="UserType">Loại người dùng (claim u, ví dụ 'svc' cho service token).</param>
/// <param name="IsAdmin">Cờ xác định vai trò quản trị viên.</param>
public sealed record ReviewActorContext(
    Guid ActorId,
    string? UserType = null,
    bool IsAdmin = false)
{
    /// <summary>
    /// Kiểm tra token này có phải là service token (u = 'svc') hay không.
    /// </summary>
    public bool IsServiceToken => string.Equals(UserType, "svc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tạo ngữ cảnh người dùng thông thường.
    /// </summary>
    public static ReviewActorContext Create(Guid actorId, string? userType = null, bool isAdmin = false) =>
        new(actorId, userType, isAdmin);
}
