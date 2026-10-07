using CatalogService.Application.Services;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;

namespace Dprental.CatalogService.Application.Services;

public sealed class CanonicalEquipmentTypeRules(IEquipmentTypeSchemaRepository repository)
{
    /// <summary>Dùng khi tạo, sửa danh sách category và publish.</summary>
    public async Task<CanonicalCheck> CheckCategoriesAsync(
        IReadOnlyCollection<Guid> categoryIds,
        IReadOnlyCollection<Guid>? existingCategoryIds = null, // Bổ sung tham số này
        CancellationToken cancellationToken = default)
    {
        Guid[] ids = categoryIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return CanonicalCheck.CategoryRequired;
        }

        IReadOnlyList<Category> categories = await repository.GetCategoriesAsync(ids, cancellationToken);
        if (categories.Count != ids.Length)
        {
            return CanonicalCheck.CategoryNotFound;
        }

        // Lọc: Chỉ kiểm tra IsActive với các category MỚI (không nằm trong existingCategoryIds)
        bool hasNewInactiveCategory = categories
            .Where(c => existingCategoryIds == null || !existingCategoryIds.Contains(c.Id))
            .Any(category => !category.IsActive);

        return hasNewInactiveCategory ? CanonicalCheck.CategoryInactive : CanonicalCheck.Ok;
    }

    /// <summary>Dùng khi tạo mới hoặc đổi tên/category. Truyền excludeId khi sửa để không tự trùng với chính nó.</summary>
    public async Task<CanonicalCheck> CheckAsync(
        string name,
        IReadOnlyCollection<Guid> categoryIds,
        Guid? excludeId,
        IReadOnlyCollection<Guid>? existingCategoryIds = null, // Bổ sung tham số này
        CancellationToken cancellationToken = default)
    {
        CanonicalCheck categoryCheck = await CheckCategoriesAsync(categoryIds, existingCategoryIds, cancellationToken);
        if (categoryCheck != CanonicalCheck.Ok)
        {
            return categoryCheck;
        }

        string normalized = CatalogNormalizer.NormalizeName(name);
        bool duplicate = await repository.ExistsCanonicalDuplicateAsync(
            normalized, categoryIds.Distinct().ToArray(), excludeId, cancellationToken);
        return duplicate ? CanonicalCheck.DuplicateName : CanonicalCheck.Ok;
    }
}