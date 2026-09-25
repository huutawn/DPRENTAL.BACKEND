namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Cung cấp thông tin danh tính đã được xác thực của người dùng hoặc service đang gọi API.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>
    /// ID người dùng đã được xác thực (từ claim sub/NameIdentifier hoặc header tin cậy).
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Tên của service đã được xác thực (từ service token claim service_name/client_id hoặc header tin cậy).
    /// </summary>
    string? ServiceName { get; }

    /// <summary>
    /// Xác định xem request hiện tại đã được xác thực hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }
}
