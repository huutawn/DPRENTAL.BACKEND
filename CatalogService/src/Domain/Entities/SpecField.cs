using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.ValueObjects;
using System.Text.RegularExpressions;

namespace Dprental.CatalogService.Domain.Entities;

public sealed partial class SpecField
{
    private readonly List<SpecOption> _options = [];

    public SpecField()
    {
    }

    public Guid Id { get; private set; }
    public Guid SchemaVersionId { get; private set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SpecDataType DataType { get; private set; }
    public string? Unit { get; private set; }
    public bool IsRequired { get; set; }
    public decimal? MinValue { get; private set; }
    public decimal? MaxValue { get; private set; }
    public int DisplayOrder { get; private set; }
    public IReadOnlyCollection<SpecOption> Options => _options;

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex KeyPattern();

    internal static SpecField Create(Guid schemaVersionId, SpecFieldDraft draft, int displayOrder)
    {
        string key = draft.Key.Trim();
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Key bắt đầu bằng chữ thường, chỉ gồm chữ và số (ví dụ capacityKg), tối đa 64 ký tự.", nameof(draft));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Label);

        bool numeric = draft.DataType is SpecDataType.Number or SpecDataType.WholeNumber;
        if (!numeric && (draft.MinValue is not null || draft.MaxValue is not null))
        {
            throw new ArgumentException("MinValue/MaxValue chỉ dùng cho kiểu number và integer.", nameof(draft));
        }

        if (draft.MinValue is not null && draft.MaxValue is not null && draft.MinValue > draft.MaxValue)
        {
            throw new ArgumentException("MinValue không được lớn hơn MaxValue.", nameof(draft));
        }

        IReadOnlyList<SpecOptionDraft> options = draft.Options ?? [];
        if (draft.DataType == SpecDataType.Enum && options.Count == 0)
        {
            throw new ArgumentException("Field kiểu enum phải có ít nhất một option.", nameof(draft));
        }

        if (draft.DataType != SpecDataType.Enum && options.Count > 0)
        {
            throw new ArgumentException("Chỉ field kiểu enum mới có option.", nameof(draft));
        }

        if (options.Select(option => option.Value.Trim()).Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            throw new ArgumentException("Giá trị option bị trùng.", nameof(draft));
        }

        SpecField field = new()
        {
            Id = Guid.NewGuid(),
            SchemaVersionId = schemaVersionId,
            Key = key,
            Label = draft.Label.Trim(),
            DataType = draft.DataType,
            Unit = string.IsNullOrWhiteSpace(draft.Unit) ? null : draft.Unit.Trim(),
            IsRequired = draft.IsRequired,
            MinValue = draft.MinValue,
            MaxValue = draft.MaxValue,
            DisplayOrder = displayOrder
        };

        foreach (SpecOptionDraft option in options)
        {
            field._options.Add(SpecOption.Create(field.Id, option.Value, option.Label));
        }

        return field;
    }
}