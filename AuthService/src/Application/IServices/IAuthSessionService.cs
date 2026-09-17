using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Cung cấp các thao tác tạo, làm mới và thu hồi session đăng nhập.</summary>
public interface IAuthSessionService
{
    /// <summary>
    /// Tạo session và cặp token đầu tiên cho người dùng đã được xác thực bởi use case login.
    /// Hàm này không phải HTTP endpoint và sẽ được login gọi ở giai đoạn sau.
    /// </summary>
    Task<AuthTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Đổi một refresh token hợp lệ lấy cặp token mới. Token cũ chỉ được dùng đúng một lần.</summary>
    Task<AuthTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi session chứa refresh token; thao tác an toàn khi được gọi lặp lại.</summary>
    Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}
