using CatalogService.Application.IServices;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Application.Services;

public class SchemaService : ISchemaService
{
    private readonly CatalogServiceDbContext _context;

    public SchemaService(CatalogServiceDbContext context)
    {
        _context = context;
    }

    public async Task<SchemaVersion> CreateNewDraftVersionAsync(Guid specSchemaId)
    {
        SpecSchema? schema = await _context.SpecSchemas
            .Include(s => s.Versions)
                .ThenInclude(v => v.Fields)
            .FirstOrDefaultAsync(s => s.Id == specSchemaId);

        if (schema == null) throw new KeyNotFoundException("SpecSchema không tồn tại.");

        // Lấy version lớn nhất hiện tại, nếu chưa có thì bắt đầu từ 0
        var latestVersionNumber = schema.Versions.Count > 0
            ? schema.Versions.Max(v => v.VersionNumber)
            : 0;

        // Lấy version Published gần nhất để copy các trường sang bản mới
        SchemaVersion? lastPublishedVersion = schema.Versions
            .Where(v => v.Status == SchemaVersionStatus.Published)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();

        SchemaVersion newDraft = schema.CreateDraftVersion(DateTime.UtcNow);

        // Clone (Copy) toàn bộ SpecFields cũ sang bản Draft mới
        if (lastPublishedVersion != null)
        {
            foreach (SpecField field in lastPublishedVersion.Fields.OrderBy(field => field.DisplayOrder))
            {
                newDraft.AddField(new SpecFieldDraft(
                    field.Key,
                    field.Label,
                    field.DataType,
                    field.IsRequired,
                    field.Unit,
                    field.MinValue,
                    field.MaxValue,
                    field.Options.Select(option => new SpecOptionDraft(option.Value, option.Label)).ToList()));
            }
        }

        await _context.SaveChangesAsync();

        return newDraft;
    }

    public async Task PublishSchemaVersionAsync(Guid versionId)
    {
        var versionToPublish = await _context.SchemaVersions
            .Include(v => v.SpecSchema)
                .ThenInclude(s => s!.SchemaVersions)
            .Include(v => v.SpecSchema)
                .ThenInclude(s => s!.EquipmentType)
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (versionToPublish == null)
            throw new KeyNotFoundException("Schema Version không tồn tại.");

        if (versionToPublish.Status != SchemaVersionStatus.Draft)
            throw new InvalidOperationException("Chỉ có thể Publish version đang ở trạng thái Draft.");

        var currentActiveVersion = versionToPublish.SpecSchema!.SchemaVersions
            .FirstOrDefault(v => v.Status == SchemaVersionStatus.Published);

        if (currentActiveVersion != null)
        {
            currentActiveVersion.Status = SchemaVersionStatus.Deprecated;
        }

        versionToPublish.Status = SchemaVersionStatus.Published;
        versionToPublish.PublishedAtUtc = DateTime.UtcNow;

        versionToPublish.SpecSchema!.EquipmentType!.ActiveSchemaVersionId = versionToPublish.Id;

        await _context.SaveChangesAsync();
    }

    public async Task UpdateSpecFieldAsync(Guid versionId, Guid fieldId, SpecField updateData)
    {
        var version = await _context.SchemaVersions
            .Include(v => v.SpecFields)
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null) throw new KeyNotFoundException("Version không tồn tại.");

        if (version.Status == SchemaVersionStatus.Published)
            throw new InvalidOperationException("Không thể sửa Schema đã Published.");

        if (version.Status == SchemaVersionStatus.Deprecated)
            throw new InvalidOperationException("Không thể sửa Schema đã Deprecated.");

        // Dùng fieldId truyền vào từ URL để tìm kiếm
        var existingField = version.SpecFields.FirstOrDefault(f => f.Id == fieldId);
        if (existingField != null)
        {
            existingField.Label = updateData.Label;
            existingField.IsRequired = updateData.IsRequired;
            // Cập nhật các trường khác...
        }

        await _context.SaveChangesAsync();
    }
}