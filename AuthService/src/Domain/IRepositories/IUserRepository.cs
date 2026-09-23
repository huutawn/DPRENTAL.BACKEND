using Dprental.AuthService.Common.Repositories;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>
/// Repository của User. <see cref="IRepo{TEntity, TKey}.CreateAsync"/> ném
/// <see cref="Exceptions.DuplicateEmailException"/> khi email đã tồn tại.
/// </summary>
public interface IUserRepository : IRepo<User, Guid>
{
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default);
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    /// <summary>Đọc user active kèm role, permission và membership (organization) để dựng hồ sơ.</summary>
    Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách user có phân trang và tổng số lượng.</summary>
    Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, CancellationToken cancellationToken = default);
}
