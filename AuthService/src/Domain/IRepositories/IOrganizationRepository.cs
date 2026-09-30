using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Find the approved organization (Active or Suspended) that owns this slug.</summary>
    Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Check whether an approved organization (Active or Suspended) owns this slug.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Không truyền <paramref name="status"/>: sắp theo tên. Có truyền: chỉ lấy đúng trạng thái đó, cũ nhất trước
    /// để hàng chờ duyệt được xử lý theo thứ tự nộp.
    /// </summary>
    Task<PagedResult<Organization>> GetAllAsync(PageRequest request, OrganizationStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc organization dạng tracked và khóa row (SELECT ... FOR UPDATE) tới hết transaction hiện tại,
    /// để kiểm tra trạng thái rồi ghi mà không bị request khác chen vào giữa. Phải gọi bên trong <see cref="BeginTransactionAsync"/>.
    /// </summary>
    Task<Organization?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo organization và membership ban đầu trong một transaction.
    /// Không chứa business validation — chỉ đảm bảo tính nguyên tử DB.
    /// </summary>
    Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default);

    /// <summary>Check whether an approved organization (Active or Suspended) owns this tax code.</summary>
    Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default);

    /// <summary>
    /// Khóa (row-level lock) các organization liên quan theo thứ tự cố định để chống race condition và deadlock khi thay đổi Owner.
    /// </summary>
    Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default);

    Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
