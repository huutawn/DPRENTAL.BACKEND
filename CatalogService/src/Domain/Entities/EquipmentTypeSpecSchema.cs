using Dprental.CatalogService.Domain.Validation;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Đặc tả kỹ thuật có phiên bản của EquipmentType.
/// PublishedAt = null biểu thị trạng thái Draft.
/// </summary>
public class EquipmentTypeSpecSchema
{
    public Guid Id { get; private set; }

    public Guid EquipmentTypeId { get; private set; }

    public int Version { get; private set; }

    public EquipmentTypeSpecDefinition Definition { get; private set; } = new();

    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; private set; }

    public EquipmentType EquipmentType { get; private set; } = null!;

    // EF Core constructor
    private EquipmentTypeSpecSchema()
    {
    }

    /// <summary>
    /// Factory tạo mới bản nháp specification schema (Draft, PublishedAt = null).
    /// </summary>
    public static EquipmentTypeSpecSchema CreateDraft(
        Guid equipmentTypeId,
        int version,
        EquipmentTypeSpecDefinition definition,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version phải lớn hơn 0.");
        }

        var validationResult = SpecSchemaDefinitionValidator.Validate(definition);
        if (validationResult.IsFailure)
        {
            throw new InvalidOperationException(validationResult.Error.Message);
        }

        return new EquipmentTypeSpecSchema
        {
            Id = Guid.NewGuid(),
            EquipmentTypeId = equipmentTypeId,
            Version = version,
            Definition = definition,
            CreatedAt = AsUtc(utcNow),
            PublishedAt = null
        };
    }

    /// <summary>
    /// Factory tạo bản ghi schema đã công khai (phục vụ migration hoặc test seeding).
    /// </summary>
    public static EquipmentTypeSpecSchema CreatePublished(
        Guid id,
        Guid equipmentTypeId,
        int version,
        EquipmentTypeSpecDefinition definition,
        DateTime createdAtUtc,
        DateTime publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validationResult = SpecSchemaDefinitionValidator.Validate(definition);
        if (validationResult.IsFailure)
        {
            throw new InvalidOperationException(validationResult.Error.Message);
        }

        return new EquipmentTypeSpecSchema
        {
            Id = id,
            EquipmentTypeId = equipmentTypeId,
            Version = version,
            Definition = definition,
            CreatedAt = AsUtc(createdAtUtc),
            PublishedAt = AsUtc(publishedAtUtc)
        };
    }

    /// <summary>
    /// Cập nhật toàn bộ definition của bản nháp. Chặn sửa nếu schema đã Published.
    /// </summary>
    public void UpdateDefinition(EquipmentTypeSpecDefinition newDefinition, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(newDefinition);
        if (PublishedAt.HasValue)
        {
            throw new InvalidOperationException("Không thể chỉnh sửa schema đã công khai.");
        }

        var validationResult = SpecSchemaDefinitionValidator.Validate(newDefinition);
        if (validationResult.IsFailure)
        {
            throw new InvalidOperationException(validationResult.Error.Message);
        }

        Definition = newDefinition;
    }

    /// <summary>
    /// Công khai schema từ trạng thái Draft. Chặn publish lại nếu đã Published.
    /// </summary>
    public void Publish(DateTime utcNow)
    {
        if (PublishedAt.HasValue)
        {
            throw new InvalidOperationException("Schema đã được công khai trước đó.");
        }

        var validationResult = SpecSchemaDefinitionValidator.Validate(Definition);
        if (validationResult.IsFailure)
        {
            throw new InvalidOperationException(validationResult.Error.Message);
        }

        PublishedAt = AsUtc(utcNow);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
