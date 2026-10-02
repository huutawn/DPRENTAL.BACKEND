using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

namespace Dprental.CatalogService.Application.Validators;

public sealed class UpdateEquipmentTypeRequestValidator
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected via DI.")]
    public Result Validate(UpdateEquipmentTypeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.HasAnyFieldSpecified)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidPayload);
        }

        if (request.IsCodeSpecified)
        {
            var codeResult = EquipmentTypeValidationRules.ValidateCode(request.Code);
            if (codeResult.IsFailure)
            {
                return codeResult;
            }
        }

        if (request.IsNameSpecified)
        {
            var nameResult = EquipmentTypeValidationRules.ValidateName(request.Name);
            if (nameResult.IsFailure)
            {
                return nameResult;
            }
        }

        if (request.IsDescriptionSpecified)
        {
            var descResult = EquipmentTypeValidationRules.ValidateDescription(request.Description);
            if (descResult.IsFailure)
            {
                return descResult;
            }
        }

        if (request.IsCategoryIdsSpecified)
        {
            var catResult = EquipmentTypeValidationRules.ValidateCategoryIds(request.CategoryIds);
            if (catResult.IsFailure)
            {
                return catResult;
            }
        }

        if (request.IsMediaSpecified)
        {
            // Explicit null media policy: omission giữ nguyên; explicit null bị reject; [] hợp lệ
            if (request.Media is null)
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
            }

            var mediaValidation = EquipmentTypeValidationRules.ValidateMedia(request.Media);
            if (mediaValidation.IsFailure)
            {
                return mediaValidation;
            }
        }

        return Result.Success();
    }
}
