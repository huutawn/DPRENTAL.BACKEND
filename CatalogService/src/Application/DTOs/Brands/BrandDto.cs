using Dprental.CatalogService.Application.Common;
using System.Text.Json.Serialization;
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
    DateTime? UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ListingCount = null)
{
    public static BrandDto FromEntity(Brand entity) => FromEntity(entity, null);

    public static BrandDto FromEntity(Brand entity, int? listingCount)
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
            entity.UpdatedAt,
            listingCount);
    }
}
