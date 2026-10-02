namespace CatalogService.Application.DTOs.Category;

public sealed record CreateCategoryRequest(
    string Name,
    string? Description,
    Guid? ImageMediaId = null
);
