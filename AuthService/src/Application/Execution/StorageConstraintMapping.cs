using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Execution;

/// <summary>
/// Ánh xạ chính xác các vi phạm ràng buộc unique DB sang expected error tương ứng.
/// Chỉ các constraint được nhận diện chính xác mới ánh xạ sang 409 Conflict.
/// </summary>
public static class StorageConstraintMapping
{
    public static bool TryMap(StorageConstraintViolationException exception, [NotNullWhen(true)] out Error? expectedError)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.ViolationType != StorageConstraintViolationType.Unique || string.IsNullOrWhiteSpace(exception.ConstraintName))
        {
            expectedError = null;
            return false;
        }

        expectedError = exception.ConstraintName switch
        {
            StorageConstraints.UserEmail => AuthErrors.User.EmailAlreadyExists,
            StorageConstraints.UserCitizenId => AuthErrors.User.CitizenIdAlreadyExists,
            StorageConstraints.UserRolePrimaryKey => AuthErrors.Role.AlreadyAssigned,
            StorageConstraints.OrganizationSlug => AuthErrors.Organization.SlugAlreadyExists,
            StorageConstraints.OrganizationTaxCode => AuthErrors.Organization.TaxCodeAlreadyExists,
            StorageConstraints.MembershipUserOrganization => AuthErrors.Organization.MembershipAlreadyExists,
            StorageConstraints.ExternalLoginProviderSubject => AuthErrors.ExternalLogin.AlreadyLinked,
            StorageConstraints.ExternalLoginUserProvider => AuthErrors.ExternalLogin.AlreadyLinked,
            _ => null
        };

        return expectedError is not null;
    }
}
