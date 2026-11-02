using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed class SupplierContactService(ISupplierContactRepository repository)
{
    public async Task<Result<SupplierContactResponse>> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            return Result.Failure<SupplierContactResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        var contact = await repository.FindAsync(organizationId, cancellationToken);
        if (contact is null || contact.Type != OrganizationType.Supplier || contact.Status != OrganizationStatus.Active)
        {
            return Result.Failure<SupplierContactResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        return Result.Success(new SupplierContactResponse(contact.Name, contact.ContactPhone));
    }
}
