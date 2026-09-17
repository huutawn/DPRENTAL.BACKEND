namespace AuthService.Application.Authentication.Sessions;

/// <summary>Cung cấp các thao tác tạo, làm mới và thu hồi session đăng nhập.</summary>
public interface IAuthenticationSessionService
{
    /// <summary>
    /// Tạo session và cặp token đầu tiên cho người dùng đã được xác thực bởi use case login.
    /// Hàm này không phải HTTP endpoint và sẽ được login gọi ở giai đoạn sau.
    /// </summary>
    Task<AuthenticationTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Đổi một refresh token hợp lệ lấy cặp token mới. Token cũ chỉ được dùng đúng một lần.</summary>
    Task<AuthenticationTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi session chứa refresh token; thao tác an toàn khi được gọi lặp lại.</summary>
    Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>Cặp access token và refresh token được trả cho client.</summary>
public sealed record AuthenticationTokenPair(string AccessToken, string RefreshToken);
