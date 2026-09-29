using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>
/// Repository của User. Chỉ đọc, ghi và bảo đảm tính nguyên tử của thao tác DB.
/// </summary>
public interface IUserRepository : IRepo<User, Guid>
{
    Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default);

    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default);

    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, string? type, CancellationToken cancellationToken = default);

    /// <summary>Thực hiện đổi mật khẩu và thu hồi toàn bộ session của user trong một transaction nguyên tử.</summary>
    Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>Tiêu thụ token khôi phục mật khẩu (dùng 1 lần), cập nhật mật khẩu và thu hồi mọi session trong một transaction.</summary>
    Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra user tồn tại và đang active.</summary>
    Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default);
}
