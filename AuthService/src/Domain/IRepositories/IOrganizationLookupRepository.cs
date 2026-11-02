using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Domain.IRepositories;

public sealed record OrganizationPublicRecord(Guid Id, string Name, string Slug, OrganizationType Type,
    OrganizationStatus Status, string? ContactPhone, Guid? LogoMediaId);

public interface IOrganizationLookupRepository
{
    Task<OrganizationPublicRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationPublicRecord>> FindByOwnerAsync(Guid userId, CancellationToken cancellationToken = default);
}
