using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Domain.Repositories;

/// <summary>
/// Bản cài đặt tạm khi SpecSchema chưa tồn tại: không type nào có schema đã publish.
/// Hệ quả: equipment type bật RequiresSpecSchema chưa publish được cho tới khi SpecSchema được triển khai.
/// </summary>
public sealed class NoSpecSchemaCatalog : ISpecSchemaCatalog
{
    public Task<bool> HasPublishedSchemaAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
