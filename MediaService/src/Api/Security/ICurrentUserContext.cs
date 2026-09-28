namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Cung cấp thông tin danh tính đã được xác thực của người dùng hoặc service đang gọi API.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>
    /// ID người dùng đã được xác thực từ claim sub của user token.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// ID service lấy từ claim sub của service token đã xác thực.
    /// </summary>
    string? ServiceId { get; }

    /// <summary>Danh sách scope từ các claim s của service token đã xác thực.</summary>
    IReadOnlySet<string> ServiceScopes { get; }

    /// <summary>
    /// Xác định xem request hiện tại đã được xác thực hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }
}
