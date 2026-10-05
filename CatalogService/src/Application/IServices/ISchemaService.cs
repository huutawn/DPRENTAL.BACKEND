using Dprental.CatalogService.Domain.Entities;

namespace CatalogService.Application.IServices;

public interface ISchemaService
{
    // Tạo bản nháp (Draft) mới từ phiên bản hiện tại (v1 -> v2)
    Task<SchemaVersion> CreateNewDraftVersionAsync(Guid specSchemaId);

    // Đăng bố (Publish) một bản Draft thành phiên bản chính thức
    Task PublishSchemaVersionAsync(Guid versionId);

    // Cập nhật thông tin một trường (chỉ cho phép sửa khi là Draft)
    Task UpdateSpecFieldAsync(Guid versionId, Guid fieldId, SpecField updateData);
}