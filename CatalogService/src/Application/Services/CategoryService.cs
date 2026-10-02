using CatalogService.Application.DTOs.Category;
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
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(ICategoryRepository repository, ILogger<CategoryService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<CategoryDetailResponse>> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Create", null, await CreateCoreAsync(request, cancellationToken));
    }

    public async Task<Result<CategoryDetailResponse>> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Logged("Update", id, await UpdateCoreAsync(id, request, cancellationToken));
    }

    public async Task<IEnumerable<CategoryDetailResponse>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _repository.GetAllActiveAsync(cancellationToken);

        return categories.Select(ToResponse);
    }

    public async Task<Result<CategoryDetailResponse>> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await _repository.GetByIdAsync(id, cancellationToken);

        // Danh mục inactive (xóa mềm) được trả về như không tồn tại
        if (category == null || !category.IsActive)
        {
            return CatalogErrors.Category.NotFound;
        }

        return ToResponse(category);
    }

    private async Task<Result<CategoryDetailResponse>> CreateCoreAsync(CreateCategoryRequest request, CancellationToken cancellationToken)
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
            Name = request.Name,
            Slug = slug,
            Description = request.Description,
            IsActive = true
        };

        // 4. Lưu DB
        await _repository.AddAsync(category, cancellationToken);

        return ToResponse(category);
    }

    private async Task<Result<CategoryDetailResponse>> UpdateCoreAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken)
    {
        if (request.Name != null)
        {
            Result nameResult = CategoryValidationRules.ValidateName(request.Name);
            if (nameResult.IsFailure)
            {
                return nameResult.Error;
            }
        }

        Result descriptionResult = CategoryValidationRules.ValidateDescription(request.Description);
        if (descriptionResult.IsFailure)
        {
            return descriptionResult.Error;
        }

        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null)
        {
            return CatalogErrors.Category.NotFound;
        }

        if (request.Name != null) category.Name = request.Name;
        if (request.Description != null) category.Description = request.Description;
        if (request.IsActive.HasValue) category.IsActive = request.IsActive.Value;

        await _repository.UpdateAsync(category, cancellationToken);

        return ToResponse(category);
    }

    private static CategoryDetailResponse ToResponse(Category category) =>
        new(category.Id, category.Name, category.Slug, category.Description, category.IsActive);

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
