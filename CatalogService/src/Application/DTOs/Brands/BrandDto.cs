using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Application.DTOs.Brands;

/// <summary>
/// DTO biểu diễn dữ liệu của Brand trên toàn platform.
/// </summary>
public sealed record BrandDto(
    Guid Id,
    string Code,
    string Name,
    string Slug,
    Guid? LogoMediaId,
    string? LogoUrl,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt)
{
    public static BrandDto FromEntity(Brand entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new BrandDto(
            entity.Id,
            entity.Code,
            entity.Name,
            entity.Slug,
            entity.LogoMediaId,
            MediaUrlResolver.ResolvePublicUrl(entity.LogoMediaId),
            entity.IsActive,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
