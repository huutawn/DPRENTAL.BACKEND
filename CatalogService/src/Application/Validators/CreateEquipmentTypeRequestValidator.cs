using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

namespace Dprental.CatalogService.Application.Validators;

public sealed class CreateEquipmentTypeRequestValidator
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected via DI.")]
    public Result Validate(CreateEquipmentTypeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var codeResult = EquipmentTypeValidationRules.ValidateCode(request.Code);
        if (codeResult.IsFailure)
        {
            return codeResult;
        }

        var nameResult = EquipmentTypeValidationRules.ValidateName(request.Name);
        if (nameResult.IsFailure)
        {
            return nameResult;
        }

        var descResult = EquipmentTypeValidationRules.ValidateDescription(request.Description);
        if (descResult.IsFailure)
        {
            return descResult;
        }

        var catResult = EquipmentTypeValidationRules.ValidateCategoryIds(request.CategoryIds);
        if (catResult.IsFailure)
        {
            return catResult;
        }

        // Enforce media-null policy: omission giữ nguyên/default empty; explicit null bị reject
        if (request.IsMediaSpecified && request.Media is null)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        if (request.Media is null)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        var mediaResult = EquipmentTypeValidationRules.ValidateMedia(request.Media);
        if (mediaResult.IsFailure)
        {
            return mediaResult;
        }

        return Result.Success();
    }
}
