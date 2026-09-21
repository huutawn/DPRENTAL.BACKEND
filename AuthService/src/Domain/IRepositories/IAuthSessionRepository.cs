using Dprental.AuthService.Common.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>Repository duy nhất quản lý session và refresh token.</summary>
public interface IAuthSessionRepository : IRepo<Session, Guid>
{
    /// <summary>Tạo session mới cùng refresh token đầu tiên nếu user đang hoạt động.</summary>
    Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Xoay refresh token một cách nguyên tử. Nếu phát hiện token đã dùng lại,
    /// Repository sẽ thu hồi session trước khi trả kết quả.
    /// </summary>
    Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Thu hồi session được xác định bởi hash refresh token.</summary>
    Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken);

    Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
