using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.User;
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

    /// <summary>Lấy danh sách các phiên đăng nhập của người dùng, đánh dấu phiên hiện tại theo refreshToken.</summary>
    Task<IReadOnlyList<SessionResponse>> GetSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi một phiên đăng nhập cụ thể của người dùng.</summary>
    Task<Result> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Tra cứu SessionId từ chuỗi refresh token raw nếu hợp lệ.</summary>
    Task<Guid?> GetCurrentSessionIdAsync(string? currentRefreshToken, CancellationToken cancellationToken = default);
}
