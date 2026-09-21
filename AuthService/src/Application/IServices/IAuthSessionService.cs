using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Cung cấp các thao tác tạo, làm mới và thu hồi session đăng nhập.</summary>
public interface IAuthSessionService
{
    Task<AuthTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Tạo ticket Google một lần dùng để Next BFF exchange token pair.</summary>
    Task<string?> CreateGoogleLoginTicketAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Đổi ticket Google một lần lấy cặp access/refresh token.</summary>
    Task<AuthTokenPair?> ExchangeGoogleLoginTicketAsync(string ticket, CancellationToken cancellationToken = default);

    Task<AuthTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
