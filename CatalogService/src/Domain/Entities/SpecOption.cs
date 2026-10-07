namespace Dprental.CatalogService.Domain.Entities;

public sealed class SpecOption
{
    private SpecOption()
    {
    }

    public Guid Id { get; private set; }
    public Guid SpecFieldId { get; private set; }
    public string Value { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;

    internal static SpecOption Create(Guid specFieldId, string value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return new SpecOption { Id = Guid.NewGuid(), SpecFieldId = specFieldId, Value = value.Trim(), Label = label.Trim() };
    }
}