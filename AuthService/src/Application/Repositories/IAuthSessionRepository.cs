using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Repositories;

/// <summary>
/// Repository quản lý session và refresh token: chỉ đọc, ghi, cập nhật có điều kiện và transaction.
/// Không chứa quyết định nghiệp vụ (reuse detection, status checks, permission merging).
/// </summary>
public interface IAuthSessionRepository : IRepo<Session, Guid>
{
    Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Tạo Session và RefreshToken mới cho user.</summary>
    Task CreateSessionAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Tạo ticket Google ngắn hạn cho user.</summary>
    Task<bool> CreateGoogleLoginTicketAsync(
        Guid userId,
        string ticketHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tiêu thụ ticket Google (dùng 1 lần) và tạo session/refresh token trong cùng một transaction.
    /// Trả về UserId nếu tiêu thụ thành công, hoặc null nếu ticket không hợp lệ / đã được dùng.
    /// </summary>
    Task<Guid?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
        string ticketHash,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy thông tin RefreshToken kèm Session và User để Service kiểm tra trạng thái và phát hiện reuse.</summary>
    Task<RefreshToken?> GetRefreshTokenWithSessionAndUserAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xoay vòng refresh token bằng cập nhật có điều kiện (conditional update) và thêm token mới trong transaction.
    /// Trả về false nếu token đã bị dùng hoặc thu hồi trước đó (race condition).
    /// </summary>
    Task<bool> RotateTokenAsync(
        Guid currentTokenId,
        Guid sessionId,
        Guid userId,
        string replacementHash,
        DateTime sessionExpiresAtUtc,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task RevokeByRefreshTokenHashAsync(
        string refreshTokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task<Guid?> GetUserIdByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Session>> GetSessionsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Guid?> GetSessionIdByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeSessionForUserAsync(
        Guid userId,
        Guid sessionId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
