using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Cung cấp các thao tác tạo, làm mới và thu hồi session đăng nhập. Toàn bộ phương thức trả Result/Result&lt;T&gt;.</summary>
public interface IAuthSessionService
{
    Task<Result<AuthTokenPair>> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Result<AuthTokenPair>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<Result> RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<Result> RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách các phiên đăng nhập của người dùng, đánh dấu phiên hiện tại theo refreshToken hoặc sessionId.</summary>
    Task<Result<IReadOnlyList<SessionResponse>>> GetSessionsAsync(Guid userId, string? currentRefreshToken, Guid? currentSessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi một phiên đăng nhập cụ thể của người dùng.</summary>
    Task<Result> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Tra cứu SessionId từ chuỗi refresh token raw nếu hợp lệ.</summary>
    Task<Result<Guid>> GetCurrentSessionIdAsync(string? currentRefreshToken, CancellationToken cancellationToken = default);
}
