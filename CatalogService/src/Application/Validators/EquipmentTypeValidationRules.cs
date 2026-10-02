using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

namespace Dprental.CatalogService.Application.Validators;

public static class EquipmentTypeValidationRules
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 4000;

    /// <summary>Mỗi media mới là một lần gọi verify sang Media Service, nên số lượng phải có trần.</summary>
    public const int MaxMediaCount = 20;

    public static Result ValidateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > MaxCodeLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidCode);
        }

        return Result.Success();
    }

    public static Result ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidName);
        }

        return Result.Success();
    }

    public static Result ValidateDescription(string? description)
    {
        if (description != null && description.Length > MaxDescriptionLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidDescription);
        }

        return Result.Success();
    }

    public static Result ValidateCategoryId(Guid? categoryId)
    {
        if (!categoryId.HasValue || categoryId.Value == Guid.Empty)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidCategory);
        }

        return Result.Success();
    }

    public static Result ValidateMedia(IReadOnlyList<EquipmentTypeMediaItemDto?> media)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (media.Count > MaxMediaCount)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        // 1. Kiểm tra null element trước khi đọc bất kỳ property nào của item
        if (media.Any(m => m is null))
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        if (media.Any(m => m!.MediaId == Guid.Empty))
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        if (media.Any(m => m!.DisplayOrder < 0))
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        if (media.Select(m => m!.MediaId).Distinct().Count() != media.Count)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        if (media.Count(m => m!.IsThumbnail) > 1)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidMedia);
        }

        return Result.Success();
    }
}
