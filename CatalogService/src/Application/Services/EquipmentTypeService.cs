using CatalogService.Application.Services;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.CatalogService.Application.Services;

public sealed partial class EquipmentTypeService(
    IEquipmentTypeRepository repository,
    ICategoryRepository categoryRepository,
    IMediaServiceClient mediaServiceClient,
    EquipmentTypeListQueryValidator listQueryValidator,
    CreateEquipmentTypeRequestValidator createValidator,
    UpdateEquipmentTypeRequestValidator updateValidator,
    CanonicalEquipmentTypeRules canonicalRules,
    ICatalogCallerContext caller,
    ILogger<EquipmentTypeService> logger) : IEquipmentTypeService
{
    public async Task<Result<PagedResult<EquipmentTypeListItemDto>>> SearchAsync(
        GetEquipmentTypesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validationResult = listQueryValidator.Validate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }


        PagedResult<EquipmentTypeListItemDto> pagedResult = await repository.SearchPublishedAsync(request, cancellationToken);
        return EnrichList(pagedResult);
    }

    public async Task<Result<PagedResult<EquipmentTypeAdminListItemDto>>> SearchAdminAsync(
        GetAdminEquipmentTypesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<IReadOnlyList<EquipmentTypeStatus>> statusesResult = listQueryValidator.Validate(request);
        if (statusesResult.IsFailure)
        {
            return statusesResult.Error;
        }

        PagedResult<EquipmentTypeAdminListItemDto> paged =
            await repository.SearchAdminAsync(request, statusesResult.Value, cancellationToken);

        var enrichedItems = paged.Items
            .Select(item => item with
            {
                ThumbnailUrl = MediaUrlResolver.ResolvePublicUrl(item.ThumbnailMediaId),
                Categories = item.Categories
                    .Select(c => c with { ImageUrl = MediaUrlResolver.ResolvePublicUrl(c.ImageMediaId) })
                    .ToList()
            })
            .ToList();

        return new PagedResult<EquipmentTypeAdminListItemDto>(enrichedItems, paged.Page, paged.PageSize, paged.TotalCount);
    }

    public async Task<Result<EquipmentTypeDetailDto>> GetAdminDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeDetailDto? detail = await repository.GetDetailByIdAsync(id, cancellationToken);
        if (detail is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        return EnrichDetail(detail);
    }

    public async Task<Result<EquipmentTypeDetailDto>> GetPublishedDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeDetailDto? detail = await repository.GetPublishedByIdAsync(id, cancellationToken);
        if (detail is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        return EnrichDetail(detail);
    }

    public async Task<Result<EquipmentTypeDetailDto>> CreateAsync(
        CreateEquipmentTypeRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Create", null, await CreateCoreAsync(request, operatorUserId, cancellationToken));
    }

    public async Task<Result<EquipmentTypeDetailDto>> UpdateAsync(
        Guid id,
        UpdateEquipmentTypeRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Update", id, await UpdateCoreAsync(id, request, operatorUserId, cancellationToken));
    }

    public async Task<Result<EquipmentTypeDetailDto>> PublishAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Logged("Publish", id, await PublishCoreAsync(id, cancellationToken));

    public async Task<Result<EquipmentTypeDetailDto>> DisableAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Logged("Disable", id, await DisableCoreAsync(id, cancellationToken));

    private async Task<Result<EquipmentTypeDetailDto>> CreateCoreAsync(
        CreateEquipmentTypeRequest request,
        string operatorUserId,
        CancellationToken cancellationToken)
    {
        Result validationResult = createValidator.Validate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }
        CanonicalCheck canonicalCheck = await canonicalRules.CheckAsync(
            request.Name,
            request.CategoryIds,
            excludeId: null,
            cancellationToken: cancellationToken);

        if (canonicalCheck != CanonicalCheck.Ok)
        {
            return MapCanonicalError(canonicalCheck);
        }



        var categories = new List<Category>();
        foreach (var categoryId in request.CategoryIds)
        {
            var category = await categoryRepository.GetByIdAsync(categoryId, cancellationToken);
            if (category is null)
            {
                return CatalogErrors.EquipmentType.CategoryNotFound;
            }

            if (!category.IsActive)
            {
                return CatalogErrors.EquipmentType.CategoryInactive;
            }

            categories.Add(category);
        }

        string trimmedCode = request.Code.Trim();
        if (await repository.ExistsByCodeAsync(trimmedCode, null, cancellationToken))
        {
            return CatalogErrors.EquipmentType.DuplicateCode;
        }

        // XÃ¡c minh toÃ n bá»™ media trÆ°á»›c khi public
        var distinctMediaIds = request.Media?
            .Where(m => m != null)
            .Select(m => m!.MediaId)
            .Distinct()
            .ToList() ?? [];

        if (distinctMediaIds.Count > 0)
        {
            var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaListAsync(
                mediaServiceClient,
                distinctMediaIds,
                operatorUserId,
                cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        var equipmentType = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Code = trimmedCode,
            Name = request.Name.Trim(),
            Description = request.Description,
            CreatedAt = DateTime.UtcNow,
            Categories = categories
        };

        var saveResult = await repository.AddAsync(equipmentType, request.Media, request.SpecSchema, cancellationToken);
        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        return EnrichDetail(saveResult.Value);
    }

    private async Task<Result<EquipmentTypeDetailDto>> UpdateCoreAsync(
    Guid id,
    UpdateEquipmentTypeRequest request,
    string operatorUserId,
    CancellationToken cancellationToken)
    {
        Result validationResult = updateValidator.Validate(request);

        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        EquipmentType? entity =
            await repository.GetByIdAsync(id, cancellationToken);

        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }


        // ------------------------------------------------------------
        // XÃ¡c Ä‘á»‹nh Name + Categories sau khi update
        // ------------------------------------------------------------
        string effectiveName = request.IsNameSpecified
            ? request.Name!.Trim()
            : entity.Name;
        Guid[] effectiveCategoryIds = request.IsCategoryIdsSpecified
            ? request.CategoryIds!.Distinct().ToArray()
            : entity.Categories
                .Select(c => c.Id)
                .Distinct()
                .ToArray();
        // ------------------------------------------------------------
        // Canonical validation
        // ------------------------------------------------------------
        var existingCategoryIds = entity.Categories.Select(c => c.Id).ToList();
        CanonicalCheck canonicalCheck =
            await canonicalRules.CheckAsync(
                effectiveName,
                effectiveCategoryIds,
                excludeId: id,
                existingCategoryIds: existingCategoryIds,
                cancellationToken);


        if (canonicalCheck != CanonicalCheck.Ok)
        {
            return MapCanonicalError(canonicalCheck);
        }
        // ------------------------------------------------------------
        // Categories
        // ------------------------------------------------------------
        if (request.IsCategoryIdsSpecified)
        {
            var newCategories = new List<Category>();

            foreach (Guid categoryId in effectiveCategoryIds)
            {
                Category? category =
                    await categoryRepository.GetByIdAsync(
                        categoryId,
                        cancellationToken);

                if (category is null)
                {
                    return CatalogErrors.EquipmentType.CategoryNotFound;
                }

                if (!category.IsActive && !existingCategoryIds.Contains(categoryId))
                {
                    return CatalogErrors.EquipmentType.CategoryInactive;
                }

                newCategories.Add(category);
            }

            entity.Categories.Clear();

            foreach (Category category in newCategories)
            {
                entity.Categories.Add(category);
            }
        }
        // ------------------------------------------------------------
        // Code
        // ------------------------------------------------------------
        if (request.IsCodeSpecified)
        {
            string trimmedCode = request.Code!.Trim();

            if (await repository.ExistsByCodeAsync(
                    trimmedCode,
                    id,
                    cancellationToken))
            {
                return CatalogErrors.EquipmentType.DuplicateCode;
            }

            entity.Code = trimmedCode;
        }
        // ------------------------------------------------------------
        // Name
        // ------------------------------------------------------------
        if (request.IsNameSpecified)
        {
            entity.Name = request.Name!.Trim();
        }
        // ------------------------------------------------------------
        // Description
        // ------------------------------------------------------------
        if (request.IsDescriptionSpecified)
        {
            entity.Description = request.Description;
        }
        // ------------------------------------------------------------
        // Media
        // ------------------------------------------------------------

        if (request.IsMediaSpecified)
        {
            // Validator Ä‘Ã£ reject null.
            IReadOnlyList<EquipmentTypeMediaItemDto> desiredMedia =
                request.Media!
                    .Where(m => m is not null)
                    .Select(m => m!)
                    .ToList();

            var desiredMediaIds = desiredMedia
                .Select(m => m.MediaId)
                .Distinct()
                .ToList();

            var existingMediaIds = entity.Media
                .Select(m => m.MediaId)
                .ToHashSet();

            var newlyAddedMediaIds = desiredMediaIds
                .Where(mediaId => !existingMediaIds.Contains(mediaId))
                .ToList();

            // Verify media má»›i
            if (newlyAddedMediaIds.Count > 0)
            {
                var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaListAsync(
                    mediaServiceClient,
                    newlyAddedMediaIds,
                    operatorUserId,
                    cancellationToken);
                if (mediaResult.IsFailure)
                {
                    return mediaResult.Error;
                }
            }
        }

        // ------------------------------------------------------------
        // UpdatedAt
        // ------------------------------------------------------------

        entity.UpdatedAt = DateTime.UtcNow;

        // ------------------------------------------------------------
        // Save
        // ------------------------------------------------------------

        IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaToSave =
            request.IsMediaSpecified
                ? request.Media
                : null;

        Result<EquipmentTypeDetailDto> saveResult =
            await repository.SaveAsync(
                entity,
                mediaToSave,
                cancellationToken);

        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        return EnrichDetail(saveResult.Value);
    }
    private static Result<EquipmentTypeDetailDto> MapCanonicalError(
    CanonicalCheck check)
    {
        return check switch
        {
            CanonicalCheck.CategoryRequired =>
                CatalogErrors.EquipmentType.CategoryRequired,

            CanonicalCheck.CategoryNotFound =>
                CatalogErrors.EquipmentType.CategoryNotFound,

            CanonicalCheck.CategoryInactive =>
                CatalogErrors.EquipmentType.CategoryInactive,

            CanonicalCheck.DuplicateName =>
                CatalogErrors.EquipmentType.DuplicateCanonicalName,

            CanonicalCheck.Ok =>
                throw new ArgumentException(
                    "Canonical check must not be Ok.",
                    nameof(check)),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(check),
                    check,
                    null)
        };
    }

    private async Task<Result<EquipmentTypeDetailDto>> PublishCoreAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await repository.GetByIdAsync(id, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }


        if (!entity.CurrentSchemaVersion.HasValue)
        {
            return CatalogErrors.EquipmentType.SchemaRequired;
        }

        if (entity.Status == EquipmentTypeStatus.Published)
        {
            return CatalogErrors.EquipmentType.InvalidStateTransition;
        }

        if (entity.Categories.Count == 0 || !entity.Categories.Any(c => c.IsActive))
        {
            return CatalogErrors.EquipmentType.CategoryInactive;
        }

        entity.Publish(DateTime.UtcNow);
        var saveResult = await repository.SaveStateTransitionAsync(entity, cancellationToken);
        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        return EnrichDetail(saveResult.Value);
    }

    private async Task<Result<EquipmentTypeDetailDto>> DisableCoreAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await repository.GetByIdAsync(id, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }



        if (entity.Status != EquipmentTypeStatus.Published)
        {
            return CatalogErrors.EquipmentType.InvalidStateTransition;
        }

        entity.Disable(DateTime.UtcNow);
        var saveResult = await repository.SaveStateTransitionAsync(entity, cancellationToken);
        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        return EnrichDetail(saveResult.Value);
    }


    private static PagedResult<EquipmentTypeListItemDto> EnrichList(PagedResult<EquipmentTypeListItemDto> paged)
    {
        var enrichedItems = paged.Items
            .Select(item => item with
            {
                ThumbnailUrl = MediaUrlResolver.ResolvePublicUrl(item.ThumbnailMediaId),
                Categories = item.Categories
                    .OrderBy(c => c.Name)
                    .ThenBy(c => c.Id)
                    .Select(c => c with { ImageUrl = MediaUrlResolver.ResolvePublicUrl(c.ImageMediaId) })
                    .ToList()
            })
            .ToList();

        return new PagedResult<EquipmentTypeListItemDto>(enrichedItems, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static EquipmentTypeDetailDto EnrichDetail(EquipmentTypeDetailDto detail)
    {
        return detail with
        {
            Categories = detail.Categories
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => c with { ImageUrl = MediaUrlResolver.ResolvePublicUrl(c.ImageMediaId) })
                .ToList(),
            Media = detail.Media
                .OrderBy(m => m.DisplayOrder)
                .ThenBy(m => m.MediaId)
                .Select(m => m with { Url = MediaUrlResolver.ResolvePublicUrl(m.MediaId) })
                .ToList(),
            PrimarySpecs = EquipmentTypeDetailDto.ExtractPrimarySpecs(detail.SpecSchema?.Definition)
        };
    }

    private Result<EquipmentTypeDetailDto> Logged(string operation, Guid? equipmentTypeId, Result<EquipmentTypeDetailDto> result)
    {
        if (result.IsSuccess)
        {
            LogSucceeded(logger, operation, result.Value.Id, result.Value.Status);
        }
        else
        {
            LogFailed(logger, operation, equipmentTypeId, result.Error.Code);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "EquipmentType {Operation} succeeded. EquipmentTypeId: {EquipmentTypeId}, Status: {Status}")]
    private static partial void LogSucceeded(ILogger logger, string operation, Guid equipmentTypeId, EquipmentTypeStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EquipmentType {Operation} failed. EquipmentTypeId: {EquipmentTypeId}, ErrorCode: {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, string operation, Guid? equipmentTypeId, string errorCode);
}
