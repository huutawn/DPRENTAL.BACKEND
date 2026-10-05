using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

public sealed class CreateBrandRequestValidator
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected via DI.")]
    public Result Validate(CreateBrandRequest request, out string normalizedCode, out string normalizedName, out string normalizedSlug)
    {
        ArgumentNullException.ThrowIfNull(request);

        normalizedCode = string.Empty;
        normalizedName = string.Empty;
        normalizedSlug = string.Empty;

        var codeResult = BrandValidationRules.ValidateCode(request.Code, out normalizedCode);
        if (codeResult.IsFailure)
        {
            return codeResult;
        }

        var nameResult = BrandValidationRules.ValidateName(request.Name, out normalizedName);
        if (nameResult.IsFailure)
        {
            return nameResult;
        }

        var slugResult = BrandValidationRules.ValidateSlug(request.Slug, out normalizedSlug);
        if (slugResult.IsFailure)
        {
            return slugResult;
        }

        var logoResult = BrandValidationRules.ValidateLogoMediaId(request.LogoMediaId, request.LogoMediaId.HasValue);
        if (logoResult.IsFailure)
        {
            return logoResult;
        }

        return Result.Success();
    }
}
