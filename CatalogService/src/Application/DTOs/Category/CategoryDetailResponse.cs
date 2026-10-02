namespace CatalogService.Application.DTOs.Category;

public sealed record CategoryDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    bool IsActive,
    Guid? ImageMediaId,
    string? ImageUrl
);
