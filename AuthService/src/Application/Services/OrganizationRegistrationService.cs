using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed class OrganizationRegistrationService(
    IOrganizationRepository organizationRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    TimeProvider timeProvider) : IOrganizationRegistrationService
{
    private static readonly Guid SeededOwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private const int MaxSlugAttempts = 5;

    public async Task<Result<OrganizationRegisteredResponse>> RegisterForExistingUserAsync(
        Guid actorUserId,
        OrganizationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? actor = actorUserId == Guid.Empty ? null : await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return AuthErrors.User.NotFound;
        }

        if (ProfileCompletionGuard.Check(actor) is Error profileError)
        {
            return profileError;
        }

        if (request.Type is not (OrganizationType.Supplier or OrganizationType.Renter))
        {
            return AuthErrors.Organization.SelfRegistrationTypeNotAllowed;
        }

        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            return AuthErrors.Organization.InvalidName;
        }

        string taxCode = request.TaxCode.Trim();
        if (await organizationRepository.TaxCodeExistsAsync(taxCode, cancellationToken))
        {
            return AuthErrors.Organization.TaxCodeAlreadyExists;
        }

        Result<string> slug = await ResolveSlugAsync(request.Slug, name, cancellationToken);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        Role? ownerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Owner, cancellationToken)
            ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);
        if (ownerRole is null)
        {
            return AuthErrors.Organization.RoleNotFound;
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        Organization organization = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.Value,
            Type = request.Type,
            Status = OrganizationStatus.Pending,
            CreatedAtUtc = now,
            TaxCode = taxCode,
            BusinessLicenseNumber = CustomerAccountRules.NullIfBlank(request.BusinessLicenseNumber),
            BusinessLicenseIssuedDate = request.BusinessLicenseIssuedDate,
            BusinessLicenseIssuedPlace = CustomerAccountRules.NullIfBlank(request.BusinessLicenseIssuedPlace),
            EstablishedDate = request.EstablishedDate,
            LegalRepresentativeName = request.LegalRepresentativeName.Trim(),
            // Ưu tiên CCCD được nhập riêng (trường hợp người đại diện pháp luật khác với người đăng ký);
            // nếu không nhập, mặc định lấy CCCD đã có trong hồ sơ của actor (trường hợp phổ biến: actor tự đại diện).
            LegalRepresentativeCitizenId = CustomerAccountRules.NullIfBlank(request.LegalRepresentativeCitizenId) ?? actor.CitizenId,
            ContactEmail = UserService.NormalizeEmail(request.ContactEmail),
            ContactPhone = request.ContactPhone.Trim(),
            Address = request.Address.Trim(),
            Province = request.Province.Trim(),
            Ward = request.Ward.Trim(),
            Website = CustomerAccountRules.NullIfBlank(request.Website),
            LogoUrl = CustomerAccountRules.NullIfBlank(request.LogoUrl),
            Description = CustomerAccountRules.NullIfBlank(request.Description),
            BusinessLicenseDocumentUrl = request.BusinessLicenseDocumentUrl.Trim(),
        };

        // Membership Owner Active ngay, nhưng org Pending nên chưa đóng góp permission/organization context vào JWT.
        OrganizationMembership ownerMembership = new()
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            OrganizationId = organization.Id,
            RoleId = ownerRole.Id,
            Status = MembershipStatus.Active,
            JoinedAtUtc = now
        };

        try
        {
            await organizationRepository.CreateAsync(organization, ownerMembership, cancellationToken);
        }
        catch (DuplicateTaxCodeException)
        {
            return AuthErrors.Organization.TaxCodeAlreadyExists;
        }
        catch (DuplicateSlugException)
        {
            return AuthErrors.Organization.SlugAlreadyExists;
        }

        return new OrganizationRegisteredResponse(actor.Id, organization.Id, organization.Slug, organization.Status);
    }

    /// <summary>Slug do người dùng nhập phải hợp lệ và chưa bị dùng; slug tự sinh sẽ được thêm hậu tố khi trùng.</summary>
    private async Task<Result<string>> ResolveSlugAsync(string? requestedSlug, string name, CancellationToken cancellationToken)
    {
        string? explicitSlug = CustomerAccountRules.NullIfBlank(requestedSlug);
        if (explicitSlug is not null)
        {
            if (!OrganizationSlug.IsCanonical(explicitSlug))
            {
                return Result.Failure<string>(AuthErrors.Organization.InvalidSlugFormat);
            }

            return await organizationRepository.SlugExistsAsync(explicitSlug, cancellationToken)
                ? Result.Failure<string>(AuthErrors.Organization.SlugAlreadyExists)
                : explicitSlug;
        }

        string generated = OrganizationSlug.FromName(name);
        string candidate = generated.Length == 0 ? OrganizationSlug.WithRandomSuffix("org") : generated;
        for (int attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            if (!await organizationRepository.SlugExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }

            candidate = OrganizationSlug.WithRandomSuffix(generated);
        }

        return Result.Failure<string>(AuthErrors.Organization.SlugAlreadyExists);
    }
}
