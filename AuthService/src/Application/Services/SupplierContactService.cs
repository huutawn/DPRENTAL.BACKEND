using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed class SupplierContactService(IOrganizationLookupRepository repository, IMediaUrlResolver mediaUrlResolver)
{
    public async Task<Result<OrganizationPublicResponse>> GetByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        var ids = await repository.FindActiveSupplierIdsByOwnerAsync(ownerUserId, cancellationToken);
        if (ids.Count == 0) return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        if (ids.Count > 1) return Result.Failure<OrganizationPublicResponse>(Error.Conflict("Organization.MultipleSuppliers", "KhÃ´ng thá»ƒ xÃ¡c Ä‘á»‹nh duy nháº¥t tá»• chá»©c cá»§a nhÃ  cung cáº¥p."));
        return await GetAsync(ids[0], cancellationToken);
    }
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
