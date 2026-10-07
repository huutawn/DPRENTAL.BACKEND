namespace Dprental.CatalogService.Domain.ValueObjects;

/// <summary>Dữ liệu đầu vào để thêm một field vào version Draft.</summary>
public sealed record SpecFieldDraft(
    string Key,
    string Label,
    SpecDataType DataType,
    bool IsRequired,
    string? Unit = null,
    decimal? MinValue = null,
    decimal? MaxValue = null,
    IReadOnlyList<SpecOptionDraft>? Options = null
);