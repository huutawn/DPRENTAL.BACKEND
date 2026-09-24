using Dprental.AuthService.Common.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>Repository duy nhất quản lý session và refresh token.</summary>
public interface IAuthSessionRepository : IRepo<Session, Guid>
{
    /// <summary>Tạo session mới cùng refresh token đầu tiên nếu user đang hoạt động.</summary>
    Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>Tạo ticket Google ngắn hạn cho user đang active.</summary>
    Task<bool> CreateGoogleLoginTicketAsync(Guid userId, string ticketHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Consume ticket Google một lần và tạo session/refresh token trong cùng transaction.
    /// </summary>
    Task<AuthSessionPrincipal?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
        string ticketHash,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Xoay refresh token một cách nguyên tử. Nếu phát hiện token đã dùng lại,
    /// Repository sẽ thu hồi session trước khi trả kết quả.
    /// </summary>
    Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Thu hồi session được xác định bởi hash refresh token.</summary>
    Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);

    Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Lấy tối đa 50 session gần nhất của người dùng.</summary>
    Task<IReadOnlyList<Session>> GetSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Lấy SessionId tương ứng với tokenHash của refresh token.</summary>
    Task<Guid?> GetSessionIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi một session cụ thể của user cùng các refresh token con chưa thu hồi.</summary>
    Task<bool> RevokeSessionForUserAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default);
}
