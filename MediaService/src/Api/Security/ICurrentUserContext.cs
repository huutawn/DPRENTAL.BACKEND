namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Cung cấp thông tin danh tính đã được xác thực của người dùng đang gọi API.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>
    /// ID người dùng đã được xác thực từ claim sub của user token.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Xác định xem request hiện tại đã được xác thực hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }
}
