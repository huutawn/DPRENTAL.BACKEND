namespace AuthService.Application.Authentication.Sessions;

/// <summary>Persistence boundary do Infrastructure cài đặt cho session và refresh token.</summary>
public interface IAuthenticationSessionStore
{
    /// <summary>Tạo session mới cùng refresh token đầu tiên nếu user đang hoạt động.</summary>
    Task<SessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Xoay refresh token một cách nguyên tử. Nếu phát hiện token đã dùng lại,
    /// Infrastructure sẽ thu hồi session trước khi trả kết quả.
    /// </summary>
    Task<SessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Thu hồi session được xác định bởi hash refresh token.</summary>
    Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>Dữ liệu người dùng tối thiểu để phát hành access token.</summary>
public sealed record SessionPrincipal(Guid UserId, IReadOnlyCollection<string> Roles);
