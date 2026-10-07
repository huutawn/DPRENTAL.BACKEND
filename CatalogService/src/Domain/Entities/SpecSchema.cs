using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Dprental.CatalogService.Domain.Entities;

public sealed class SpecSchema
{
    private readonly List<SchemaVersion> _versions = [];

    private SpecSchema()
    {
    }
    public Guid Id { get; private set; }
    public Guid EquipmentTypeId { get; private set; }
    public IReadOnlyCollection<SchemaVersion> Versions => _versions.AsReadOnly();
    public EquipmentType? EquipmentType { get; set; }
    public ICollection<SchemaVersion> SchemaVersions { get; set; } = new List<SchemaVersion>();

    internal static SpecSchema Create(Guid equipmentTypeId) => 
        new() { Id = Guid.NewGuid(), EquipmentTypeId = equipmentTypeId };

    public SchemaVersion CreateDraftVersion(DateTime utcNow)
    {
        if (_versions.Any(version => version.Status == SchemaVersionStatus.Draft))
        {
            throw new InvalidOperationException("Đã có một version Draft. Hãy publish trước khi tạo bản mới.");
        }

        int next = _versions.Count == 0 ? 1 : _versions.Max(version => version.VersionNumber) + 1;
        SchemaVersion draft = SchemaVersion.CreateDraft(Id, next, utcNow);
        _versions.Add(draft);
        return draft;
    }
}