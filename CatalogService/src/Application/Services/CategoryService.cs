using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Common.Utils;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Application.Services;

public class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _repository;

    public CategoryService(ICategoryRepository repository)
    {
        _repository = repository;
    }

    public async Task<CategoryDetailResponse> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default)
    {
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

        // 5. Trả về response
        return new CategoryDetailResponse(category.Id, category.Name, category.Slug, category.Description, category.IsActive);
    }

    public async Task<CategoryDetailResponse?> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var category = await _repository.GetByIdAsync(id, cancellationToken);
        if (category == null) return null;

        // KIỂM TRA RULE: Không cho deactivate nếu đang có Published Equipment
        if (request.IsActive.HasValue && request.IsActive.Value == false && category.IsActive == true)
        {
            // TODO (Phase 2): Gọi _equipmentRepository.HasPublishedEquipmentAsync(categoryId)
            // Tạm thời để ghi chú khi nào làm tới bảng Equipment sẽ mở comment hàm check ở đây.
            // bool hasPublishedEquipment = false; 
            // if (hasPublishedEquipment) 
            // {
            //      throw new Exception("Cannot deactivate category. It contains published equipment.");
            // }
        }

        if (request.Name != null) category.Name = request.Name;
        if (request.Description != null) category.Description = request.Description;
        if (request.IsActive.HasValue) category.IsActive = request.IsActive.Value;

        await _repository.UpdateAsync(category, cancellationToken);

        return new CategoryDetailResponse(category.Id, category.Name, category.Slug, category.Description, category.IsActive);
    }

    public async Task<IEnumerable<CategoryDetailResponse>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _repository.GetAllActiveAsync(cancellationToken);

        return categories.Select(c =>
            new CategoryDetailResponse(c.Id, c.Name, c.Slug, c.Description, c.IsActive));
    }

    public async Task<CategoryDetailResponse?> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await _repository.GetByIdAsync(id, cancellationToken);

        // Trả về null nếu không tìm thấy hoặc danh mục đã bị inactive (xóa mềm)
        if (category == null || !category.IsActive) return null;

        return new CategoryDetailResponse(category.Id, category.Name, category.Slug, category.Description, category.IsActive);
    }
}