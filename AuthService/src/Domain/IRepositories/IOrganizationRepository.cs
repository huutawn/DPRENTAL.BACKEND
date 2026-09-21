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

    Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default);
}
