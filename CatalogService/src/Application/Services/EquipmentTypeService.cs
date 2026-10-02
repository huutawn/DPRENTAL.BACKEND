using Dprental.CatalogService.Application.Clients;
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
    EquipmentTypeListQueryValidator listQueryValidator,
    CreateEquipmentTypeRequestValidator createValidator,
    UpdateEquipmentTypeRequestValidator updateValidator,
    IMediaVerificationClient mediaVerificationClient,
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
        return pagedResult;
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

        return detail;
    }

    public async Task<Result<EquipmentTypeDetailDto>> CreateAsync(
        CreateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Create", null, await CreateCoreAsync(request, cancellationToken));
    }

    public async Task<Result<EquipmentTypeDetailDto>> UpdateAsync(
        Guid id,
        UpdateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Update", id, await UpdateCoreAsync(id, request, cancellationToken));
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
        CancellationToken cancellationToken)
    {
        Result validationResult = createValidator.Validate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var category = await categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.EquipmentType.CategoryNotFound;
        }

        if (!category.IsActive)
        {
            return CatalogErrors.EquipmentType.CategoryInactive;
        }

        string trimmedCode = request.Code.Trim();
        if (await repository.ExistsByCodeAsync(trimmedCode, null, cancellationToken))
        {
            return CatalogErrors.EquipmentType.DuplicateCode;
        }

        Result mediaResult = await VerifyMediaAsync(request.Media!.Select(m => m!.MediaId), cancellationToken);
        if (mediaResult.IsFailure)
        {
            return mediaResult.Error;
        }

        var equipmentType = new EquipmentType
        {
            Id = Guid.NewGuid(),
            CategoryId = request.CategoryId,
            Code = trimmedCode,
            Name = request.Name.Trim(),
            Description = request.Description,
            CreatedAt = DateTime.UtcNow,
            Category = category
        };

        return await repository.AddAsync(equipmentType, request.Media, cancellationToken);
    }

    private async Task<Result<EquipmentTypeDetailDto>> UpdateCoreAsync(
        Guid id,
        UpdateEquipmentTypeRequest request,
        CancellationToken cancellationToken)
    {
        Result validationResult = updateValidator.Validate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var entity = await repository.GetByIdAsync(id, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (request.IsCategoryIdSpecified && request.CategoryId.HasValue)
        {
            var category = await categoryRepository.GetByIdAsync(request.CategoryId.Value, cancellationToken);
            if (category is null)
            {
                return CatalogErrors.EquipmentType.CategoryNotFound;
            }

            if (!category.IsActive)
            {
                return CatalogErrors.EquipmentType.CategoryInactive;
            }

            entity.CategoryId = request.CategoryId.Value;
            entity.Category = category;
        }

        if (request.IsCodeSpecified && !string.IsNullOrWhiteSpace(request.Code))
        {
            string trimmedCode = request.Code.Trim();
            if (await repository.ExistsByCodeAsync(trimmedCode, id, cancellationToken))
            {
                return CatalogErrors.EquipmentType.DuplicateCode;
            }

            entity.Code = trimmedCode;
        }

        if (request.IsNameSpecified)
        {
            entity.Name = request.Name!.Trim();
        }

        if (request.IsDescriptionSpecified)
        {
            entity.Description = request.Description;
        }

        IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia = request.IsMediaSpecified ? request.Media : null;
        if (desiredMedia is not null)
        {
            // Media đã gắn từ trước không verify lại, để admin vẫn sắp xếp hoặc gỡ được khi media đó đã bị xóa bên Media Service.
            HashSet<Guid> attached = entity.Media.Select(m => m.MediaId).ToHashSet();
            Result mediaResult = await VerifyMediaAsync(
                desiredMedia.Select(m => m!.MediaId).Where(mediaId => !attached.Contains(mediaId)),
                cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        entity.UpdatedAt = DateTime.UtcNow;
        return await repository.SaveAsync(entity, desiredMedia, cancellationToken);
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

        if (entity.Status == EquipmentTypeStatus.Published)
        {
            return CatalogErrors.EquipmentType.InvalidStateTransition;
        }

        var category = await categoryRepository.GetByIdAsync(entity.CategoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.EquipmentType.CategoryNotFound;
        }

        if (!category.IsActive)
        {
            return CatalogErrors.EquipmentType.CategoryInactive;
        }

        entity.Publish(DateTime.UtcNow);
        return await repository.SaveStateTransitionAsync(entity, cancellationToken);
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

        // Disable được phép ngay cả khi category inactive
        entity.Disable(DateTime.UtcNow);
        return await repository.SaveStateTransitionAsync(entity, cancellationToken);
    }

    /// <summary>
    /// Xác minh media với Media Service trước khi mở transaction ghi.
    /// Chỉ cần một media không xác minh được là trả Unavailable, vì khi đó chưa biết chắc tình trạng của cả danh sách.
    /// </summary>
    private async Task<Result> VerifyMediaAsync(IEnumerable<Guid> mediaIds, CancellationToken cancellationToken)
    {
        MediaVerificationStatus[] statuses = await Task.WhenAll(
            mediaIds.Select(mediaId => mediaVerificationClient.VerifyAsync(mediaId, cancellationToken)));

        if (statuses.Contains(MediaVerificationStatus.Unavailable))
        {
            return Result.Failure(CatalogErrors.Catalog.MediaServiceUnavailable);
        }

        if (statuses.Contains(MediaVerificationStatus.NotFound))
        {
            return Result.Failure(CatalogErrors.EquipmentType.MediaNotFound);
        }

        if (statuses.Contains(MediaVerificationStatus.NotReady))
        {
            return Result.Failure(CatalogErrors.EquipmentType.MediaNotReady);
        }

        return Result.Success();
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
