namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>Repository cho role, permission của role và việc gán/thu hồi role trên user.</summary>
public interface IRoleRepository
{
    Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Mã các permission mà role này mang — dùng để kiểm tra actor có đủ quyền trước khi gán/thu hồi.</summary>
    Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Mã các permission (toàn cục, qua UserRole) mà user đang có — dùng làm cơ sở kiểm tra actor.</summary>
    Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Gán role cho user. Trả false nếu role hoặc user không tồn tại.</summary>
    Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Thu hồi role khỏi user. Trả false nếu user chưa có role đó.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);
}
