using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed class SupplierContactService(IOrganizationLookupRepository repository, IMediaUrlResolver mediaUrlResolver)
{
    public Task<IReadOnlyList<Guid>> FindActiveSupplierIdsByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        repository.FindActiveSupplierIdsByOwnerAsync(ownerUserId, cancellationToken);
    public async Task<Result<OrganizationPublicResponse>> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        var contact = await repository.FindByIdAsync(organizationId, cancellationToken);
        if (contact is null || contact.Type != OrganizationType.Supplier || contact.Status != OrganizationStatus.Active)
        {
            return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        return Result.Success(new OrganizationPublicResponse(contact.Id, contact.Name, contact.Slug, contact.Type,
            contact.Status, contact.ContactPhone, contact.LogoMediaId, mediaUrlResolver.ResolvePublicUrl(contact.LogoMediaId)));
    }
}
