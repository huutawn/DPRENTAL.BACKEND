namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>
/// Tra cứu SpecSchema của một equipment type. Tách thành interface riêng vì SpecSchema là capability độc lập;
/// khi capability đó được triển khai chỉ cần thay bản cài đặt, không đổi logic publish.
/// </summary>
public interface ISpecSchemaCatalog
{
    /// <summary>Equipment type đã có ít nhất một phiên bản SpecSchema ở trạng thái Published hay chưa.</summary>
    Task<bool> HasPublishedSchemaAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default);
}
