using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

public sealed class OrganizationRegistrationService(
    IOrganizationRepository organizationRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<OrganizationRegistrationService> logger) : IOrganizationRegistrationService
{
    private static readonly Guid SeededOwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private const int MaxSlugAttempts = 5;

    public async Task<Result<OrganizationRegisteredResponse>> RegisterForExistingUserAsync(
        Guid actorUserId,
        OrganizationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out Error? idError))
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.User.NotFound);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<OrganizationRegisteredResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RegisterOrganization",
            async () =>
            {
                User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
                if (actor is null || !actor.IsActive)
                {
                    return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.User.NotFound);
                }

                if (ProfileCompletionGuard.Check(actor) is Error profileError)
                {
                    return Result.Failure<OrganizationRegisteredResponse>(profileError);
                }

                if (request.Type is not (OrganizationType.Supplier or OrganizationType.Renter))
                {
                    return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.SelfRegistrationTypeNotAllowed);
                }

                string name = request.Name.Trim();
                if (name.Length == 0)
                {
                    return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.InvalidName);
                }

                string taxCode = request.TaxCode.Trim();
                if (await organizationRepository.TaxCodeExistsAsync(taxCode, cancellationToken))
                {
                    return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.TaxCodeAlreadyExists);
                }

                Result<string> slug = await ResolveSlugAsync(request.Slug, name, cancellationToken);
                if (slug.IsFailure)
                {
                    return Result.Failure<OrganizationRegisteredResponse>(slug.Error);
                }

                Role? ownerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Owner, cancellationToken)
                    ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);
                if (ownerRole is null)
                {
                    return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.RoleNotFound);
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
                    LegalRepresentativeCitizenId = CustomerAccountRules.NullIfBlank(request.LegalRepresentativeCitizenId)
                        ?? actor.CitizenId,
                    ContactEmail = UserService.NormalizeEmail(request.ContactEmail),
                    ContactPhone = request.ContactPhone.Trim(),
                    Address = request.Address.Trim(),
                    Province = request.Province.Trim(),
                    Ward = request.Ward.Trim(),
                    Website = CustomerAccountRules.NullIfBlank(request.Website),
                    LogoUrl = CustomerAccountRules.NullIfBlank(request.LogoUrl),
                    Description = CustomerAccountRules.NullIfBlank(request.Description),
                    BusinessLicenseDocumentUrl = request.BusinessLicenseDocumentUrl.Trim()
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

                await organizationRepository.CreateAsync(organization, ownerMembership, cancellationToken);

                return Result.Success(new OrganizationRegisteredResponse(actor.Id, organization.Id, organization.Slug, organization.Status));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    /// <summary>Slug do người dùng nhập phải hợp lệ và chưa bị dùng; slug tự sinh sẽ được thêm hậu tố khi trùng.</summary>
    private async Task<Result<string>> ResolveSlugAsync(string? requestedSlug, string name, CancellationToken cancellationToken)
    {
        string? trimmed = requestedSlug?.Trim();

        if (trimmed is not null && trimmed.Length > 0)
        {
            if (!OrganizationSlug.IsCanonical(trimmed))
            {
                return AuthErrors.Organization.InvalidSlugFormat;
            }

            if (await organizationRepository.SlugExistsAsync(trimmed, cancellationToken))
            {
                return AuthErrors.Organization.SlugAlreadyExists;
            }

            return trimmed;
        }

        string baseSlug = OrganizationSlug.FromName(name);
        string candidate = baseSlug;

        for (int attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            if (!await organizationRepository.SlugExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }

            candidate = OrganizationSlug.WithRandomSuffix(baseSlug);
        }

        return candidate;
    }
}
