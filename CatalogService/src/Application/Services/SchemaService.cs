using CatalogService.Application.DTOs.Schemas;
using CatalogService.Application.IServices;
using CatalogService.Application.Mapping;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;

namespace CatalogService.Application.Services;

/// <summary>Compatibility adapter for the original field endpoints over EquipmentTypeSpecSchema.</summary>
public sealed class SchemaService(IEquipmentTypeRepository repository) : ISchemaService
{
    public async Task<SchemaVersionDto> CreateNewDraftVersionAsync(Guid specSchemaId)
    {
        // The compatibility API uses the equipment type id as its stable schema id.
        EquipmentType? parent = await repository.GetByIdAsync(specSchemaId);
        if (parent is null)
        {
            throw new KeyNotFoundException("SpecSchema không tồn tại.");
        }

        if (await repository.GetDraftSpecSchemaEntityAsync(parent.Id) is not null)
        {
            throw new InvalidOperationException("Đã có một version Draft. Hãy publish trước khi tạo bản mới.");
        }

        IReadOnlyList<EquipmentTypeSpecSchemaDto> versions = await repository.GetSpecSchemasAsync(parent.Id);
        EquipmentTypeSpecDefinition definition = versions
            .Where(s => s.PublishedAt.HasValue)
            .OrderByDescending(s => s.Version)
            .Select(s => s.Definition)
            .FirstOrDefault() ?? new EquipmentTypeSpecDefinition();

        Result<EquipmentTypeSpecSchemaDto> addResult =
            await repository.AddSpecSchemaDraftAsync(parent, definition);
        if (addResult.IsFailure)
        {
            throw new InvalidOperationException(addResult.Error.Message);
        }

        EquipmentTypeSpecSchema? draft = await repository.GetSpecSchemaEntityByIdAsync(addResult.Value.Id);
        if (draft is null)
        {
            throw new InvalidOperationException("Không thể đọc lại version Draft vừa tạo.");
        }

        return SchemaCompatibilityMapper.ToDto(draft, parent.CurrentSchemaVersion);
    }

    public async Task PublishSchemaVersionAsync(Guid versionId)
    {
        (EquipmentType parent, EquipmentTypeSpecSchema schema) = await GetDraftAsync(versionId);
        Result<EquipmentTypeSpecSchemaDto> result = await repository.PublishSpecSchemaAsync(parent, schema);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    public async Task UpdateSpecFieldAsync(Guid versionId, Guid fieldId, SpecField updateData)
    {
        ArgumentNullException.ThrowIfNull(updateData);
        (EquipmentType parent, EquipmentTypeSpecSchema schema) = await GetDraftAsync(versionId);
        var fields = schema.Definition.Fields.ToList();
        int index = fields.FindIndex(f => SchemaCompatibilityMapper.StableId(schema.Id, f.Key) == fieldId);
        if (index < 0)
        {
            return;
        }

        fields[index] = ToDefinitionField(
            updateData.Key,
            updateData.Label,
            SpecDataTypeMapper.ToContract(updateData.DataType),
            updateData.IsRequired,
            updateData.Unit,
            updateData.MinValue,
            updateData.MaxValue,
            fields[index].DisplayOrder,
            updateData.Options.Select(o => new EquipmentTypeSpecOption { Value = o.Value, Label = o.Label }).ToList());
        await SaveDefinitionAsync(parent, schema, fields);
    }

    public async Task<SpecFieldDto> AddFieldAsync(
        Guid versionId,
        AddSpecFieldRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SpecDataTypeMapper.TryParse(request.DataType, out SpecDataType dataType))
        {
            throw new ArgumentException("dataType chỉ nhận: number, integer, boolean, enum, text.");
        }

        (EquipmentType parent, EquipmentTypeSpecSchema schema) = await GetDraftAsync(versionId, cancellationToken);
        var fields = schema.Definition.Fields.ToList();
        EquipmentTypeSpecField field = ToDefinitionField(
            request.Key,
            request.Label,
            SpecDataTypeMapper.ToContract(dataType),
            request.IsRequired,
            request.Unit,
            request.MinValue,
            request.MaxValue,
            fields.Count == 0 ? 1 : fields.Max(f => f.DisplayOrder) + 1,
            request.Options?.Select(o => new EquipmentTypeSpecOption { Value = o.Value, Label = o.Label }).ToList());
        fields.Add(field);
        await SaveDefinitionAsync(parent, schema, fields, cancellationToken);
        return SchemaCompatibilityMapper.ToFieldDto(schema.Id, field);
    }

    public async Task RemoveFieldAsync(Guid versionId, Guid fieldId, CancellationToken cancellationToken = default)
    {
        (EquipmentType parent, EquipmentTypeSpecSchema schema) = await GetDraftAsync(versionId, cancellationToken);
        var fields = schema.Definition.Fields
            .Where(f => SchemaCompatibilityMapper.StableId(schema.Id, f.Key) != fieldId)
            .ToList();
        if (fields.Count == schema.Definition.Fields.Count)
        {
            throw new KeyNotFoundException("Field không thuộc version này.");
        }

        await SaveDefinitionAsync(parent, schema, fields, cancellationToken);
    }

    private async Task<(EquipmentType Parent, EquipmentTypeSpecSchema Schema)> GetDraftAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeSpecSchema? schema = await repository.GetSpecSchemaEntityByIdAsync(versionId, cancellationToken);
        if (schema is null)
        {
            throw new KeyNotFoundException("Schema Version không tồn tại.");
        }

        if (schema.PublishedAt.HasValue)
        {
            throw new InvalidOperationException("Không thể sửa Schema đã Published.");
        }

        EquipmentType? parent = await repository.GetByIdAsync(schema.EquipmentTypeId, cancellationToken);
        if (parent is null)
        {
            throw new KeyNotFoundException("EquipmentType không tồn tại.");
        }

        return (parent, schema);
    }

    private async Task SaveDefinitionAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema schema,
        IReadOnlyList<EquipmentTypeSpecField> fields,
        CancellationToken cancellationToken = default)
    {
        var definition = new EquipmentTypeSpecDefinition { Fields = fields };
        Result<EquipmentTypeSpecSchemaDto> result =
            await repository.UpdateSpecSchemaDraftAsync(parent, schema, definition, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    private static EquipmentTypeSpecField ToDefinitionField(
        string key,
        string label,
        string dataType,
        bool required,
        string? unit,
        decimal? min,
        decimal? max,
        int displayOrder,
        IReadOnlyList<EquipmentTypeSpecOption>? options) => new()
        {
            Key = key,
            Label = label,
            DataType = dataType,
            Required = required,
            Unit = unit,
            Min = min,
            Max = max,
            DisplayOrder = displayOrder,
            Options = options
        };
}
