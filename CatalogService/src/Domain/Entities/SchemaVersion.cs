using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.ValueObjects;

namespace Dprental.CatalogService.Domain.Entities;

public sealed class SchemaVersion
{
    private readonly List<SpecField> _fields = [];

    private SchemaVersion()
    {
    }

    public Guid Id { get; set; }
    public Guid SpecSchemaId { get; set; }
    public int VersionNumber { get; set; }
    public SchemaVersionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public IReadOnlyCollection<SpecField> Fields => _fields;
    public ICollection<SpecField> SpecFields { get; set; } = new List<SpecField>();
    public SpecSchema? SpecSchema { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    internal static SchemaVersion CreateDraft(Guid specSchemaId, int versionNumber, DateTime utcNow) =>
        new()
        {
            Id = Guid.NewGuid(),
            SpecSchemaId = specSchemaId,
            VersionNumber = versionNumber,
            Status = SchemaVersionStatus.Draft,
            CreatedAt = utcNow
        };

    public SpecField AddField(SpecFieldDraft draft)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(draft);

        string key = draft.Key.Trim();
        if (_fields.Any(field => string.Equals(field.Key, key, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Field '{key}' đã tồn tại trong version này.");
        }

        int displayOrder = _fields.Count == 0 ? 1 : _fields.Max(field => field.DisplayOrder) + 1;
        SpecField field = SpecField.Create(Id, draft, displayOrder);
        _fields.Add(field);
        return field;
    }

    /// <summary>Sau khi publish, version bất biến vì các tài sản của Supplier tham chiếu tới nó.</summary>
    public void Publish(DateTime utcNow)
    {
        EnsureDraft();
        if (_fields.Count == 0)
        {
            throw new InvalidOperationException("Không thể publish version chưa có field nào.");
        }

        Status = SchemaVersionStatus.Published;
        PublishedAt = utcNow;
    }

    public void Retire()
    {
        if (Status != SchemaVersionStatus.Published)
        {
            throw new InvalidOperationException("Chỉ Retire được version đã Published.");
        }

        Status = SchemaVersionStatus.Retired;
    }

    private void EnsureDraft()
    {
        if (Status != SchemaVersionStatus.Draft)
        {
            throw new InvalidOperationException("Chỉ sửa được version ở trạng thái Draft.");
        }
    }
}