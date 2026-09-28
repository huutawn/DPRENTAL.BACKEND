using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default);

    Task<PagedResult<Organization>> GetAllAsync(PageRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo organization và membership ban đầu trong một transaction.
    /// Không chứa business validation — chỉ đảm bảo tính nguyên tử DB.
    /// </summary>
    Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default);

    Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default);

    /// <summary>
    /// Khóa (row-level lock) các organization liên quan theo thứ tự cố định để chống race condition và deadlock khi thay đổi Owner.
    /// </summary>
    Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default);

    Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
