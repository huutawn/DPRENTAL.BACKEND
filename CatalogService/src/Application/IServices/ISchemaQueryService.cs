using CatalogService.Application.DTOs.Schemas;

namespace CatalogService.Application.IServices;

public interface ISchemaQueryService
{
    /// <summary>Admin: danh sách mọi version (gồm Draft) cùng SpecSchemaId và VersionId.</summary>
    Task<SchemaOverviewDto?> GetOverviewAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default);

    /// <summary>Admin: đọc một version bất kỳ trạng thái, kể cả Deprecated.</summary>
    Task<SchemaVersionDto?> GetVersionAsync(
        Guid equipmentTypeId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>Public: loại thiết bị phải Published. Mặc định bản active, hoặc bản Published/Deprecated theo số version. Không trả Draft.</summary>
    Task<SchemaVersionDto?> GetPublicSchemaAsync(
        Guid equipmentTypeId, int? versionNumber, CancellationToken cancellationToken = default);
}