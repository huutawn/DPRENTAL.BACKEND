using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

public sealed class UpdateBrandRequestValidator
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected via DI.")]
    public Result Validate(UpdateBrandRequest request, out string? normalizedCode, out string? normalizedName, out string? normalizedSlug)
    {
        ArgumentNullException.ThrowIfNull(request);

        normalizedCode = null;
        normalizedName = null;
        normalizedSlug = null;

        if (!request.HasAnyFieldSpecified)
        {
            return Result.Failure(CatalogErrors.Brand.InvalidPayload);
        }

        if (request.IsCodeSpecified)
        {
            var codeResult = BrandValidationRules.ValidateCode(request.Code, out var code);
            if (codeResult.IsFailure)
            {
                return codeResult;
            }
            normalizedCode = code;
        }

        if (request.IsNameSpecified)
        {
            var nameResult = BrandValidationRules.ValidateName(request.Name, out var name);
            if (nameResult.IsFailure)
            {
                return nameResult;
            }
            normalizedName = name;
        }

        if (request.IsSlugSpecified)
        {
            var slugResult = BrandValidationRules.ValidateSlug(request.Slug, out var slug);
            if (slugResult.IsFailure)
            {
                return slugResult;
            }
            normalizedSlug = slug;
        }

        if (request.IsLogoMediaIdSpecified)
        {
            var logoResult = BrandValidationRules.ValidateLogoMediaId(request.LogoMediaId, true);
            if (logoResult.IsFailure)
            {
                return logoResult;
            }
        }

        return Result.Success();
    }
}
