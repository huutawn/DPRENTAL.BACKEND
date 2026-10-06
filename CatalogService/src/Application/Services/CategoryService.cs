using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Utils;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.CatalogService.Application.Services;

public partial class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _repository;
    private readonly IMediaServiceClient _mediaServiceClient;
    private readonly CategoryListQueryValidator _listQueryValidator;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(
        ICategoryRepository repository,
        IMediaServiceClient mediaServiceClient,
        CategoryListQueryValidator listQueryValidator,
        ILogger<CategoryService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _mediaServiceClient = mediaServiceClient ?? throw new ArgumentNullException(nameof(mediaServiceClient));
        _listQueryValidator = listQueryValidator ?? throw new ArgumentNullException(nameof(listQueryValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<CategoryDetailResponse>> CreateCategoryAsync(
        CreateCategoryRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Create", null, await CreateCoreAsync(request, operatorUserId, cancellationToken));
    }

    public async Task<Result<CategoryDetailResponse>> UpdateCategoryAsync(
        Guid id,
        UpdateCategoryRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Update", id, await UpdateCoreAsync(id, request, operatorUserId, cancellationToken));
    }

    public async Task<IEnumerable<CategoryDetailResponse>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _repository.GetAllActiveAsync(cancellationToken);
        return categories.Select(ToResponse);
    }

    public async Task<Result<CategoryDetailResponse>> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidId;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null || !category.IsActive)
        {
            return CatalogErrors.Category.NotFound;
        }

        return ToResponse(category);
    }

    public async Task<Result<PagedResult<CategoryDetailResponse>>> SearchAdminAsync(
        GetAdminCategoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = _listQueryValidator.Validate(request.Page, request.PageSize, request.Keyword, request.Sort);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var query = new CategorySearchQuery
        {
            Keyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : request.Keyword.Trim(),
            IsActive = request.IsActive,
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort
        };

        var pagedEntities = await _repository.SearchAsync(query, cancellationToken);
        var dtos = pagedEntities.Items.Select(ToResponse).ToList();

        return new PagedResult<CategoryDetailResponse>(dtos, pagedEntities.Page, pagedEntities.PageSize, pagedEntities.TotalCount);
    }

    public async Task<Result<CategoryDetailResponse>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidId;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null)
        {
            return CatalogErrors.Category.AdminNotFound;
        }

        return ToResponse(category);
    }

    public async Task<Result<CategoryDetailResponse>> DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Logged("Disable", id, await DisableCoreAsync(id, cancellationToken));
    }

    public async Task<Result<CategoryDetailResponse>> EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Logged("Enable", id, await EnableCoreAsync(id, cancellationToken));
    }

    private async Task<Result<CategoryDetailResponse>> DisableCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidId;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null)
        {
            return CatalogErrors.Category.AdminNotFound;
        }

        // Idempotent: nếu đã inactive thì không thay đổi UpdatedAtUtc và không gọi DB update
        if (!category.Disable(DateTime.UtcNow))
        {
            return ToResponse(category);
        }

        await _repository.UpdateAsync(category, cancellationToken);
        return ToResponse(category);
    }

    private async Task<Result<CategoryDetailResponse>> EnableCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidId;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null)
        {
            return CatalogErrors.Category.AdminNotFound;
        }

        // Idempotent: nếu đã active thì không thay đổi UpdatedAtUtc và không gọi DB update
        if (!category.Enable(DateTime.UtcNow))
        {
            return ToResponse(category);
        }

        await _repository.UpdateAsync(category, cancellationToken);
        return ToResponse(category);
    }
    private async Task<Result<CategoryDetailResponse>> CreateCoreAsync(
        CreateCategoryRequest request,
        string operatorUserId,
        CancellationToken cancellationToken)
    {
        Result nameResult = CategoryValidationRules.ValidateName(request.Name);
        if (nameResult.IsFailure)
        {
            return nameResult.Error;
        }

        Result descriptionResult = CategoryValidationRules.ValidateDescription(request.Description);
        if (descriptionResult.IsFailure)
        {
            return descriptionResult.Error;
        }

        if (request.ImageMediaId.HasValue && request.ImageMediaId.Value == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidImage;
        }

        // Kiểm tra media nếu có ảnh
        if (request.ImageMediaId.HasValue)
        {
            var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaAsync(
                _mediaServiceClient,
                request.ImageMediaId.Value,
                operatorUserId,
                cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 1. Generate Slug từ Name
        var slug = SlugHelper.GenerateSlug(request.Name);

        // 2. Kiểm tra trùng Slug (Nếu trùng thì thêm timestamp vào đuôi để đảm bảo unique)
        if (await _repository.IsSlugExistsAsync(slug, cancellationToken))
        {
            slug = $"{slug}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        }

        // 3. Khởi tạo Entity (Mặc định IsActive = true)
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description,
            ImageMediaId = request.ImageMediaId,
            IsActive = true
        };

        // 4. Lưu DB
        await _repository.AddAsync(category, cancellationToken);

        return ToResponse(category);
    }

    private async Task<Result<CategoryDetailResponse>> UpdateCoreAsync(
        Guid id,
        UpdateCategoryRequest request,
        string operatorUserId,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidId;
        }

        if (request.IsNameSpecified && request.Name != null)
        {
            Result nameResult = CategoryValidationRules.ValidateName(request.Name);
            if (nameResult.IsFailure)
            {
                return nameResult.Error;
            }
        }

        if (request.IsDescriptionSpecified)
        {
            Result descriptionResult = CategoryValidationRules.ValidateDescription(request.Description);
            if (descriptionResult.IsFailure)
            {
                return descriptionResult.Error;
            }
        }

        if (request.IsImageMediaIdSpecified && request.ImageMediaId.HasValue && request.ImageMediaId.Value == Guid.Empty)
        {
            return CatalogErrors.Category.InvalidImage;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null)
        {
            return CatalogErrors.Category.NotFound;
        }

        bool hasOtherChanges = false;

        if (request.IsImageMediaIdSpecified)
        {
            if (!request.ImageMediaId.HasValue)
            {
                // Gỡ ảnh
                if (category.ImageMediaId != null)
                {
                    category.ImageMediaId = null;
                    hasOtherChanges = true;
                }
            }
            else if (category.ImageMediaId != request.ImageMediaId.Value)
            {
                // Ảnh mới gắn: xác minh và public
                var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaAsync(
                    _mediaServiceClient,
                    request.ImageMediaId.Value,
                    operatorUserId,
                    cancellationToken);
                if (mediaResult.IsFailure)
                {
                    return mediaResult.Error;
                }

                category.ImageMediaId = request.ImageMediaId.Value;
                hasOtherChanges = true;
            }
        }

        if (request.IsNameSpecified && request.Name != null)
        {
            var trimmedName = request.Name.Trim();
            if (!string.Equals(category.Name, trimmedName, StringComparison.Ordinal))
            {
                category.Name = trimmedName;
                hasOtherChanges = true;
            }
        }

        if (request.IsDescriptionSpecified)
        {
            if (!string.Equals(category.Description, request.Description, StringComparison.Ordinal))
            {
                category.Description = request.Description;
                hasOtherChanges = true;
            }
        }

        DateTime now = DateTime.UtcNow;
        bool statusChanged = false;

        if (request.IsActiveSpecified && request.IsActive.HasValue)
        {
            statusChanged = category.TransitionStatus(request.IsActive.Value, now);
        }

        if (hasOtherChanges)
        {
            category.Touch(now);
            await _repository.UpdateAsync(category, cancellationToken);
        }
        else if (statusChanged)
        {
            await _repository.UpdateAsync(category, cancellationToken);
        }

        return ToResponse(category);
    }

    private static CategoryDetailResponse ToResponse(Category category) =>
        new(
            category.Id,
            category.Name,
            category.Slug,
            category.Description,
            category.IsActive,
            category.ImageMediaId,
            MediaUrlResolver.ResolvePublicUrl(category.ImageMediaId));

    private Result<CategoryDetailResponse> Logged(string operation, Guid? categoryId, Result<CategoryDetailResponse> result)
    {
        if (result.IsSuccess)
        {
            LogSucceeded(operation, result.Value.Id);
        }
        else
        {
            LogFailed(operation, categoryId, result.Error.Code);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Category {Operation} succeeded. CategoryId: {CategoryId}")]
    private partial void LogSucceeded(string operation, Guid categoryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Category {Operation} failed. CategoryId: {CategoryId}, ErrorCode: {ErrorCode}")]
    private partial void LogFailed(string operation, Guid? categoryId, string errorCode);
}
