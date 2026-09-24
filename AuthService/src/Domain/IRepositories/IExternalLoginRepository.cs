using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>
/// Repository của liên kết đăng nhập bên ngoài. <see cref="AddAsync"/> ném
/// <see cref="Exceptions.DuplicateExternalLoginException"/> khi liên kết đã tồn tại.
/// </summary>
public interface IExternalLoginRepository
{
    /// <summary>Tìm liên kết theo nhà cung cấp và subject, kèm <see cref="User"/> để kiểm tra trạng thái tài khoản.</summary>
    Task<ExternalLogin?> FindAsync(string provider, string providerSubject, CancellationToken cancellationToken = default);

    /// <summary>Thêm liên kết mới cho một user đã tồn tại.</summary>
    Task AddAsync(ExternalLogin login, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách liên kết đăng nhập bên ngoài của một user.</summary>
    Task<IReadOnlyList<ExternalLogin>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Xoá liên kết đăng nhập bên ngoài của user theo nhà cung cấp.</summary>
    Task<bool> DeleteAsync(Guid userId, string provider, CancellationToken cancellationToken = default);
}
